<#
.SYNOPSIS
    Turns the .trx files written by `dotnet test --logger trx` into a Markdown summary.
.DESCRIPTION
    On GitHub Actions the summary goes to the run page ($env:GITHUB_STEP_SUMMARY); elsewhere it is printed.
    Some tests need real audio hardware (the "Steam Streaming" output, a tone app). Where it is missing they log
    "OMITIDA" and return without checking anything, so they count as passed. They are listed separately so a green
    run is not read as more than it is. This script only reports: the Test step decides whether the job fails.
.EXAMPLE
    dotnet test SonosFlow.sln -c Release --logger trx --results-directory TestResults
    .\.github\scripts\Write-TestSummary.ps1 -ResultsDirectory TestResults
#>
param([string] $ResultsDirectory = 'TestResults')

$ErrorActionPreference = 'Stop'
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('## Test results')
$lines.Add('')

# Test-Path first: on Windows PowerShell 5.1, -Recurse on a missing folder searches the whole parent drive.
$files = @()
if (Test-Path -LiteralPath $ResultsDirectory -PathType Container) {
    $files = @(Get-ChildItem -LiteralPath $ResultsDirectory -Filter *.trx -Recurse -File)
}
if ($files.Count -eq 0) {
    $lines.Add('No test results were found. The build or the test run probably failed before any test ran: see the log of the failed step.')
}
else {
    $rows = @(); $failed = @(); $selfSkipped = @()
    foreach ($file in $files) {
        [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
        $run = $trx.TestRun
        $project = '(unknown)'
        $firstTest = @($run.TestDefinitions.UnitTest)[0]
        # TestMethod.codeBase keeps the file name's case; UnitTest.storage is lower-cased.
        if ($firstTest -and $firstTest.TestMethod -and $firstTest.TestMethod.codeBase) { $project = [IO.Path]::GetFileNameWithoutExtension($firstTest.TestMethod.codeBase) }
        elseif ($firstTest -and $firstTest.storage) { $project = [IO.Path]::GetFileNameWithoutExtension($firstTest.storage) }
        $results = @($run.Results.UnitTestResult)
        $passed = @($results | Where-Object { $_.outcome -eq 'Passed' }).Count
        $fail = @($results | Where-Object { $_.outcome -eq 'Failed' }).Count
        $skipped = @($results | Where-Object { $_.outcome -eq 'NotExecuted' }).Count
        $seconds = ''
        if ($run.Times -and $run.Times.start -and $run.Times.finish) {
            $seconds = '{0:0.0} s' -f ([datetime]$run.Times.finish - [datetime]$run.Times.start).TotalSeconds
        }
        $rows += [pscustomobject]@{ Project = $project; Passed = $passed; Failed = $fail; Skipped = $skipped; Total = $results.Count; Time = $seconds }
        foreach ($r in $results) {
            $out = $null
            if ($r.Output) { $out = $r.Output.StdOut }
            if ($r.outcome -eq 'Failed') {
                $message = ''
                if ($r.Output -and $r.Output.ErrorInfo) { $message = ([string]$r.Output.ErrorInfo.Message).Trim() }
                $firstLine = ($message -split "`r?`n")[0]
                if ($firstLine.Length -gt 300) { $firstLine = $firstLine.Substring(0, 300) + '...' }
                $failed += [pscustomobject]@{ Project = $project; Name = $r.testName; Message = $firstLine }
            }
            elseif ($out -and ([string]$out) -match 'OMITIDA[^\r\n]*') {
                $reason = $Matches[0]
                if ($reason.Length -gt 200) { $reason = $reason.Substring(0, 200) + '...' }
                $selfSkipped += [pscustomobject]@{ Project = $project; Name = $r.testName; Reason = $reason }
            }
        }
    }

    $sum = { param($p) ($rows | Measure-Object -Property $p -Sum).Sum }
    $status = if ((& $sum 'Failed') -gt 0) { 'FAILED' } else { 'PASSED' }
    $lines.Add("**$status**: $(& $sum 'Passed') passed, $(& $sum 'Failed') failed, $(& $sum 'Skipped') skipped, $(& $sum 'Total') in total.")
    $lines.Add('')
    $lines.Add('| Project | Passed | Failed | Skipped | Total | Time |')
    $lines.Add('|---|---:|---:|---:|---:|---:|')
    foreach ($row in ($rows | Sort-Object Project)) {
        $lines.Add("| $($row.Project) | $($row.Passed) | $($row.Failed) | $($row.Skipped) | $($row.Total) | $($row.Time) |")
    }

    if ($failed.Count -gt 0) {
        $lines.Add('')
        $lines.Add('### Failed tests')
        foreach ($f in $failed) {
            $message = ($f.Message -replace '\|', '\|')
            $lines.Add("- **$($f.Project)** ``$($f.Name)``: $message")
        }
        $lines.Add('')
        $lines.Add('Full messages and stack traces: download the **test-results** artifact and open the .trx files (Visual Studio, or any text editor).')
    }

    if ($selfSkipped.Count -gt 0) {
        $lines.Add('')
        $lines.Add("### Returned early without checking anything ($($selfSkipped.Count))")
        $lines.Add('These tests log OMITIDA and stop when something they need is missing (the "Steam Streaming" output or the tone tool: GitHub runners have no audio devices) or when they are opt-in experiments. They count as passed, so run them on a PC that has that output. The reason each test gave (in Spanish):')
        foreach ($s in $selfSkipped) { $lines.Add("- **$($s.Project)** ``$($s.Name)``: $($s.Reason -replace '\|', '\|')") }
    }
}

$text = ($lines -join "`n") + "`n"
if ($env:GITHUB_STEP_SUMMARY) { [IO.File]::AppendAllText($env:GITHUB_STEP_SUMMARY, $text, (New-Object Text.UTF8Encoding($false))) }
else { $text }
