# Tune's resolver worker: keeps yt-dlp loaded and turns video ids into audio stream URLs.
# Protocol (UTF-8 JSON lines):
#   stdin : {"id": 1, "v": "<videoId>"}
#   stdout: {"id": 1, "ok": true, "url": "...", "available_at": 1794760775}
#           {"id": 1, "ok": false, "error": "..."}
#           {"ready": true, "version": "2026.10.26"}            (once, at start)
# argv: <pylib dir> <deno.exe path> <threads>
import json, os, sys, threading
from concurrent.futures import ThreadPoolExecutor

sys.path.insert(0, sys.argv[1])
import yt_dlp  # noqa: E402

DENO = sys.argv[2]
THREADS = int(sys.argv[3]) if len(sys.argv) > 3 else 3
OPTS = {
    'format': 'bestaudio', 'quiet': True, 'no_warnings': True, 'noprogress': True, 'noplaylist': True,
    'ignoreconfig': True,  # never pick up a user config (cookies etc.): Tune stays anonymous
    'js_runtimes': {'deno': {'path': DENO}},
}

_out_lock = threading.Lock()
_local = threading.local()


def emit(obj):
    line = json.dumps(obj, ensure_ascii=False)
    with _out_lock:
        sys.stdout.write(line + '\n')
        sys.stdout.flush()


def ydl():
    if not hasattr(_local, 'ydl'):
        _local.ydl = yt_dlp.YoutubeDL(OPTS)
    return _local.ydl


def resolve(req):
    rid, vid = req.get('id'), req.get('v', '')
    try:
        info = ydl().extract_info('https://music.youtube.com/watch?v=' + vid, download=False)
        url = info.get('url')
        if not url:
            raise RuntimeError('no stream URL')
        emit({'id': rid, 'ok': True, 'url': url, 'available_at': info.get('available_at') or 0})
    except Exception as e:  # report every failure to the app, never die
        msg = str(e).splitlines()[0] if str(e) else type(e).__name__
        emit({'id': rid, 'ok': False, 'error': msg})


def main():
    sys.stdout.reconfigure(encoding='utf-8')
    sys.stdin.reconfigure(encoding='utf-8')
    emit({'ready': True, 'version': yt_dlp.version.__version__})
    with ThreadPoolExecutor(max_workers=THREADS) as pool:
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                req = json.loads(line)
            except ValueError:
                continue
            pool.submit(resolve, req)


if __name__ == '__main__':
    main()
