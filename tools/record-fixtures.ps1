# Re-records InnerTube fixtures used by Tune.Core.Tests (live network).
# Usage: powershell -ExecutionPolicy Bypass -File tools\record-fixtures.ps1
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot '..\tests\Tune.Core.Tests\Fixtures'
New-Item -ItemType Directory -Force $out | Out-Null

$version = '1.' + (Get-Date).ToUniversalTime().AddDays(-7).ToString('yyyyMMdd') + '.01.00'
$headers = @{
    'User-Agent' = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36'
    'Origin'     = 'https://music.youtube.com'
    'Referer'    = 'https://music.youtube.com/'
}

function Invoke-InnerTube([string]$endpoint, [hashtable]$body) {
    $body.context = @{ client = @{ clientName = 'WEB_REMIX'; clientVersion = $version; hl = 'en'; gl = 'NP' }; user = @{} }
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $r = Invoke-WebRequest -UseBasicParsing -Method Post -Uri "https://music.youtube.com/youtubei/v1/$endpoint`?prettyPrint=false" `
        -Headers $headers -ContentType 'application/json' -Body $bytes
    # decode as UTF-8 explicitly (PS 5.1 may guess Latin-1)
    $text = [Text.Encoding]::UTF8.GetString($r.RawContentStream.ToArray())
    # continuations only work when the visitor id from an earlier response is sent back
    $vd = [regex]::Match($text, '"visitorData":"([^"]+)"')
    if ($vd.Success -and -not $headers.ContainsKey('X-Goog-Visitor-Id')) { $headers['X-Goog-Visitor-Id'] = $vd.Groups[1].Value }
    return $text
}

function Save([string]$name, [string]$text) {
    [IO.File]::WriteAllText((Join-Path $out $name), $text, (New-Object Text.UTF8Encoding $false))
    "{0,-28} {1,8:N0} bytes" -f $name, $text.Length
}

function First-Match([string]$text, [string]$pattern) {
    $m = [regex]::Match($text, $pattern)
    if (-not $m.Success) { throw "pattern not found: $pattern" }
    return $m.Groups[1].Value
}

# --- Task 3: search + radio ---
$arijit = Invoke-InnerTube 'search' @{ query = 'Arijit Singh' }
Save 'search_arijit.json' $arijit
Save 'search_sajjan.json' (Invoke-InnerTube 'search' @{ query = 'Sajjan Raj Vaidya' })

$songId = First-Match $arijit '"watchEndpoint":\{"videoId":"([A-Za-z0-9_-]{11})"'
Save 'next_radio.json' (Invoke-InnerTube 'next' @{ videoId = $songId; playlistId = "RDAMVM$songId"; isAudioOnly = $true })

# mutated copy: one list item renderer renamed, one thumbnail removed (fail-soft tests)
$mut = ([regex]'"musicResponsiveListItemRenderer"').Replace($arijit, '"fooRenderer"', 1)
$mut = ([regex]'"thumbnail":\{"musicThumbnailRenderer"').Replace($mut, '"thumbnailGone":{"musicThumbnailRenderer"', 4)
Save 'search_arijit_mutated.json' $mut

# --- Task 4: browse ---
$homeJson = Invoke-InnerTube 'browse' @{ browseId = 'FEmusic_home' }
Save 'home.json' $homeJson
$homeToken = First-Match $homeJson '"nextContinuationData":\{"continuation":"([^"]+)"'
Save 'home_continuation.json' (Invoke-InnerTube 'browse' @{ continuation = $homeToken })
$albumId  = First-Match $arijit '"browseId":"(MPRE[A-Za-z0-9_-]+)"'
$artistId = First-Match $arijit '"browseId":"(UC[A-Za-z0-9_-]{22})"'
Save 'album.json'  (Invoke-InnerTube 'browse' @{ browseId = $albumId })
Save 'artist.json' (Invoke-InnerTube 'browse' @{ browseId = $artistId })
$plId = First-Match $homeJson '"browseId":"(VL[A-Za-z0-9_-]+)"'
Save 'playlist.json' (Invoke-InnerTube 'browse' @{ browseId = $plId })

