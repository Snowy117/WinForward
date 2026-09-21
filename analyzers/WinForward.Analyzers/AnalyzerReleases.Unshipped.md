; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
WF0001 | WinForward.Lifetime | Error | Unawaited awaitable discard
WF0002 | WinForward.Lifetime | Error | Task.ContinueWith
WF0003 | WinForward.Lifetime | Error | Task.Run / Task.Factory.StartNew
WF0004 | WinForward.Lifetime | Error | Bare unawaited awaitable expression statement
