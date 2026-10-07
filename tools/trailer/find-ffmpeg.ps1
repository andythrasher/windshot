# Finds ffmpeg for the trailer scripts: the -FfmpegPath they were given, else ffmpeg on PATH
# (e.g. after `winget install Gyan.FFmpeg`), else a copy unzipped under C:\dev\tools\ffmpeg.
function Find-Ffmpeg([string]$given) {
    if ($given) {
        if (Test-Path $given -PathType Leaf) { return (Resolve-Path $given).Path }
        throw "ffmpeg not found at $given."
    }
    $onPath = Get-Command ffmpeg.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    if (Test-Path 'C:\dev\tools\ffmpeg') {
        $found = Get-ChildItem 'C:\dev\tools\ffmpeg' -Recurse -Filter ffmpeg.exe | Select-Object -First 1 -ExpandProperty FullName
        if ($found) { return $found }
    }
    throw 'ffmpeg not found. Install it (winget install Gyan.FFmpeg) or pass -FfmpegPath <path to ffmpeg.exe>.'
}
