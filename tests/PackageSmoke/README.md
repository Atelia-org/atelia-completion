# Public package smoke

Run `eng/Test-Package.ps1 -Version <version> -FeedDirectory <frozen-feed> -WorkDirectory <new-directory>`.
The script materializes this template outside the repository build configuration, then restores and runs it with a fresh NuGet cache.
`@VERSION@` in the project is replaced only in that materialized copy; this template is intentionally outside the solution.

The executable exercises the real OpenAI Chat client with an in-memory HTTP handler, plus reflected tools. No provider credentials or live endpoint are used. Separate minimal consumers prove that Diagnostics and Abstractions do not pull in the other Atelia packages.
