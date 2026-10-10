# E7 full-corpus survival (spec 0012 / ADR-0047): how many translated rows does a real SSG update
# revert, and does the per-row source guard repair them without masking changed English?
#
# Why: the 9 live tests only ever had 8 resident translations in the DAT, so the revert rate of a
# fully translated corpus was an estimate from per-SubFile metadata (1.52% for U49), never a count.
# This marks EVERY row, lets the launcher update the marked DAT, and counts what is left.
#
# Protocol (forced downgrade, as in E2 - the live game ends clean):
#   1. export the pre-update DAT (a backup copy):  export -d <pre backup> -o export-pre.txt
#   2. build the marked corpus:                     .\e7-corpus-survival.ps1 -Build -Export export-pre.txt -Out translations\e7pl.txt
#   3. patch a COPY of the pre-update DAT with it   (patch e7pl -d <copy>, from a scratch folder)
#   4. elevated: copy the patched copy over the live DAT, start the launcher, let it apply the update
#   5. export the live DAT (no elevation needed):   export -d <live DAT> -o export-after-update.txt
#   6. classify:  .\e7-corpus-survival.ps1 -Classify -Before export-pre.txt -Clean export-clean-post.txt -Measured export-after-update.txt
#      (export-clean-post.txt = the same update applied to an UNmarked DAT; it tells "SSG changed
#      this English" apart from "our row was reverted")
#   7. guard test: patch a copy of the updated DAT again with the same corpus and its ledger, export
#      it and classify again. Expected: every collateral revert is back, every changed row stays English.
#   8. elevated: copy the clean post-update DAT back over the live DAT.
#
# Rows are carved like the real parsers do (ADR-0042): two ids from the front, four tail fields
# from the back, the text is whatever sits between them.
[CmdletBinding(DefaultParameterSetName = 'Classify')]
param(
    [Parameter(ParameterSetName = 'Build', Mandatory = $true)]
    [switch]$Build,

    [Parameter(ParameterSetName = 'Build', Mandatory = $true)]
    [string]$Export,

    [Parameter(ParameterSetName = 'Build', Mandatory = $true)]
    [string]$Out,

    [Parameter(ParameterSetName = 'Classify', Mandatory = $true)]
    [switch]$Classify,

    [Parameter(ParameterSetName = 'Classify', Mandatory = $true)]
    [string]$Before,

    [Parameter(ParameterSetName = 'Classify', Mandatory = $true)]
    [string]$Clean,

    [Parameter(ParameterSetName = 'Classify', Mandatory = $true)]
    [string]$Measured
)

$Marker = '[PL] '

function Read-ExportRows
{
    param([string]$Path)
    foreach ($line in [IO.File]::ReadLines((Resolve-Path -LiteralPath $Path).ProviderPath))
    {
        if ($line.Length -eq 0 -or $line.StartsWith('#', [StringComparison]::Ordinal))
        {
            continue
        }

        $first = $line.IndexOf('||')
        $second = if ($first -ge 0) { $line.IndexOf('||', $first + 2) } else { -1 }
        if ($second -lt 0)
        {
            continue
        }

        # Walk back over the four tail separators: args_order, args_id, approved, source_digest.
        $end = $line.Length
        $cut = $end
        for ($i = 0; $i -lt 4 -and $cut -gt $second + 2; $i++)
        {
            $cut = $line.LastIndexOf('||', $cut - 1)
        }

        if ($cut -le $second)
        {
            continue
        }

        [pscustomobject]@{
            Key = $line.Substring(0, $second)
            FileId = $line.Substring(0, $first)
            Text = $line.Substring($second + 2, $cut - $second - 2)
            Tail = $line.Substring($cut + 2)
        }
    }
}

function Read-TextMap
{
    param([string]$Path)
    $map = New-Object 'System.Collections.Generic.Dictionary[string,string]'
    foreach ($row in Read-ExportRows -Path $Path)
    {
        $map[$row.Key] = $row.Text
    }
    return , $map
}

if ($Build)
{
    $alreadyMarked = 0
    $count = 0
    $outPath = if ([IO.Path]::IsPathRooted($Out)) { $Out } else { Join-Path (Get-Location) $Out }
    $writer = New-Object IO.StreamWriter($outPath, $false, (New-Object Text.UTF8Encoding($false)))
    try
    {
        $writer.Write("# E7 synthetic full corpus: every row = '$Marker' + its English source`r`n")
        foreach ($row in Read-ExportRows -Path $Export)
        {
            if ($row.Text.StartsWith($Marker.Trim(), [StringComparison]::Ordinal))
            {
                $alreadyMarked++
            }

            # Tail = args_order||args_id||approved||source_digest; force approved = 1, keep the rest.
            $parts = $row.Tail.Split(@('||'), [StringSplitOptions]::None)
            $writer.Write("$($row.Key)||$Marker$($row.Text)||$($parts[0])||$($parts[1])||1||$($parts[3])`r`n")
            $count++
        }
    }
    finally
    {
        $writer.Dispose()
    }

    Write-Host "wrote $count rows to $Out"
    if ($alreadyMarked -gt 0)
    {
        Write-Warning "$alreadyMarked English rows already start with '$($Marker.Trim())' - pick another marker, the count would be wrong."
    }

    return
}

$beforeMap = Read-TextMap -Path $Before
$cleanMap = Read-TextMap -Path $Clean
$measuredMap = Read-TextMap -Path $Measured

$categories = [ordered]@{
    'PL (marker present)' = 0
    'English, unchanged source (collateral revert)' = 0
    'English, source changed by SSG' = 0
    'removed by the update' = 0
    'PL over CHANGED English (masking!)' = 0
    'anomaly' = 0
}
$nonPlFiles = New-Object 'System.Collections.Generic.HashSet[string]'
$allFiles = New-Object 'System.Collections.Generic.HashSet[string]'

foreach ($pair in $beforeMap.GetEnumerator())
{
    $fileId = $pair.Key.Substring(0, $pair.Key.IndexOf('||'))
    [void]$allFiles.Add($fileId)
    $got = $null
    if (-not $measuredMap.TryGetValue($pair.Key, [ref]$got))
    {
        $category = 'removed by the update'
    }
    else
    {
        $cleanText = $null
        # Case-sensitive on purpose (-cne/-ceq): SSG sometimes changes only the letter case, and the
        # source guard treats that as changed English too.
        $englishChanged = $cleanMap.TryGetValue($pair.Key, [ref]$cleanText) -and $cleanText -cne $pair.Value
        if ($got -ceq ($Marker + $pair.Value))
        {
            $category = if ($englishChanged) { 'PL over CHANGED English (masking!)' } else { 'PL (marker present)' }
        }
        elseif ($got -ceq $pair.Value -and -not $englishChanged)
        {
            $category = 'English, unchanged source (collateral revert)'
        }
        elseif ($englishChanged -and $got -ceq $cleanText)
        {
            $category = 'English, source changed by SSG'
        }
        else
        {
            $category = 'anomaly'
        }
    }

    $categories[$category]++
    if ($category -ne 'PL (marker present)')
    {
        [void]$nonPlFiles.Add($fileId)
    }
}

$total = $beforeMap.Count
Write-Host "E7 classification: $total corpus rows from $Before"
foreach ($entry in $categories.GetEnumerator())
{
    Write-Host ("  {0,-48} {1,8}  ({2:P3})" -f $entry.Key, $entry.Value, ($entry.Value / [double]$total))
}

Write-Host "  SubFiles with at least one non-PL corpus row: $($nonPlFiles.Count) of $($allFiles.Count)"
$listPath = "$Measured.nonpl-fileids.txt"
[IO.File]::WriteAllLines($listPath, [string[]]($nonPlFiles | Sort-Object { [long]$_ }))
Write-Host "  non-PL FileIds written to $listPath (cross-check them against an E5 diff of the same update)"
