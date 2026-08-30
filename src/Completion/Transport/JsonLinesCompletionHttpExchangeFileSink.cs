using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Atelia.Completion.Transport;

/// <summary>
/// 将 HTTP 交换按 JSON Lines 追加写入文件，作为 MVP 阶段的 golden log 形态。
/// </summary>
/// <remarks>
/// <para>
/// 每一行都是一个 <see cref="CompletionHttpExchange"/> 的 JSON 对象，采用 camelCase 字段名。
/// </para>
/// <para>
/// 当前实现固定使用 LF 作为记录分隔符，便于后续逐行读取和顺序 replay。
/// </para>
/// <para>
/// 记录包含完整 prompt 与 provider response，不是普通应用日志。本 sink
/// 只支持 Linux x64/arm64：目标以 no-follow/non-blocking append 方式打开，
/// 再仅通过返回的 handle 验证为当前用户所有的 regular file 并收紧为 0600。
/// Windows 和其他 Unix 平台 fail closed，避免误声明等价的 no-follow 保证。
/// </para>
/// <para>
/// <c>O_NOFOLLOW</c> 保护最后一个 path component；诊断路径的父目录仍须由
/// 调用方控制。调用方应使用短期诊断路径并在完成后删除记录。
/// </para>
/// </remarks>
public sealed class JsonLinesCompletionHttpExchangeFileSink : ICompletionHttpExchangeSink {
    private const int OpenWriteOnly = 1;
    private const int OpenCreate = 0x40;
    private const int OpenAppend = 0x400;
    private const int OpenNonBlocking = 0x800;
    private const int OpenNoFollow = 0x20000;
    private const int OpenCloseOnExec = 0x80000;
    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint PermissionBitsMask = 0x0FFF;
    private const uint OwnerFileMode = 0x180; // 0600

    private static readonly JsonSerializerOptions SerializerOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly UTF8Encoding Utf8WithoutBom = new(
        encoderShouldEmitUTF8Identifier: false
    );

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly Action? _afterLinuxHandleValidatedForTest;

    public JsonLinesCompletionHttpExchangeFileSink(string filePath)
        : this(filePath, afterLinuxHandleValidatedForTest: null) { }

    internal JsonLinesCompletionHttpExchangeFileSink(
        string filePath,
        Action? afterLinuxHandleValidatedForTest
    ) {
        if (string.IsNullOrWhiteSpace(filePath)) {
            throw new ArgumentException(
                "File path must not be blank.",
                nameof(filePath)
            );
        }

        _filePath = Path.GetFullPath(filePath);
        _afterLinuxHandleValidatedForTest =
            afterLinuxHandleValidatedForTest;
    }

    public void OnExchange(CompletionHttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);
        RequireSupportedLinuxAbi();

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory)) {
            Directory.CreateDirectory(directory);
        }

        var line = JsonSerializer.Serialize(exchange, SerializerOptions) + "\n";

        lock (_gate) {
            using FileStream stream = OpenValidatedLinuxAppendStream();
            _afterLinuxHandleValidatedForTest?.Invoke();
            using var writer = new StreamWriter(
                stream,
                Utf8WithoutBom,
                bufferSize: 1024,
                leaveOpen: false
            );
            writer.Write(line);
        }
    }

    private FileStream OpenValidatedLinuxAppendStream() {
        // O_NONBLOCK does not alter regular-file append semantics. It prevents
        // a FIFO planted at the leaf from blocking before fstat can reject it.
        int descriptor = Open(
            _filePath,
            OpenWriteOnly
                | OpenCreate
                | OpenAppend
                | OpenNonBlocking
                | OpenNoFollow
                | OpenCloseOnExec,
            OwnerFileMode
        );
        if (descriptor < 0) {
            throw UnsafeTarget(Marshal.GetLastPInvokeError());
        }

        var handle = new SafeFileHandle(
            new IntPtr(descriptor),
            ownsHandle: true
        );
        try {
            LinuxFileIdentity identity = ReadIdentity(descriptor);
            uint effectiveUserId = GetEffectiveUserId();
            if (identity.FileType != RegularFileType
                || identity.OwnerUserId != effectiveUserId) {
                throw UnsafeTarget();
            }

            if (identity.PermissionBits != OwnerFileMode) {
                // Tighten the exact opened inode. A path-based chmod here
                // would recreate the check/use race this sink must avoid.
                if (ChangeMode(descriptor, OwnerFileMode) != 0) {
                    throw UnsafeTarget(Marshal.GetLastPInvokeError());
                }

                LinuxFileIdentity tightened = ReadIdentity(descriptor);
                if (tightened.Device != identity.Device
                    || tightened.Inode != identity.Inode
                    || tightened.FileType != RegularFileType
                    || tightened.OwnerUserId != effectiveUserId
                    || tightened.PermissionBits != OwnerFileMode) {
                    throw UnsafeTarget();
                }
            }

            // FileStream owns this same handle. Nothing below reopens the
            // caller-supplied path, so a concurrent rename cannot redirect
            // prompt bytes after validation.
            return new FileStream(
                handle,
                FileAccess.Write,
                bufferSize: 4096,
                isAsync: false
            );
        }
        catch {
            handle.Dispose();
            throw;
        }
    }

    private static LinuxFileIdentity ReadIdentity(int descriptor) {
        IntPtr buffer = Marshal.AllocHGlobal(256);
        try {
            if (Fstat(descriptor, buffer) != 0) {
                throw UnsafeTarget(Marshal.GetLastPInvokeError());
            }

            (int modeOffset, int userIdOffset) =
                RuntimeInformation.ProcessArchitecture switch {
                    Architecture.X64 => (24, 28),
                    Architecture.Arm64 => (16, 24),
                    _ => throw new PlatformNotSupportedException(
                        "Completion raw exchange logs require the supported Linux stat ABI."
                    )
                };
            uint rawMode = unchecked((uint)Marshal.ReadInt32(
                buffer,
                modeOffset
            ));
            return new LinuxFileIdentity(
                Device: unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                Inode: unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                FileType: rawMode & FileTypeMask,
                OwnerUserId: unchecked((uint)Marshal.ReadInt32(
                    buffer,
                    userIdOffset
                )),
                PermissionBits: rawMode & PermissionBitsMask
            );
        }
        finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void RequireSupportedLinuxAbi() {
        if (!OperatingSystem.IsLinux()
            || RuntimeInformation.ProcessArchitecture is not (
                Architecture.X64 or Architecture.Arm64)) {
            throw new PlatformNotSupportedException(
                "Completion raw exchange logs require Linux x64/arm64 no-follow file semantics."
            );
        }
    }

    private static InvalidOperationException UnsafeTarget(int? error = null) {
        string suffix = error is null ? string.Empty : $" (errno {error})";
        return new InvalidOperationException(
            "Completion raw exchange log must be a no-follow, current-user-owned regular file with Unix mode 0600; symbolic links and special files are rejected"
                + suffix
                + "."
        );
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int Fstat(int descriptor, IntPtr value);

    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int ChangeMode(int descriptor, uint mode);

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    private readonly record struct LinuxFileIdentity(
        ulong Device,
        ulong Inode,
        uint FileType,
        uint OwnerUserId,
        uint PermissionBits
    );
}
