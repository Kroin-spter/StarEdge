"""Serve the Unity Web build with the headers required by Cesium threads."""
import argparse
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit


class UnityWebHandler(SimpleHTTPRequestHandler):
    def guess_type(self, path):
        """Return the original resource MIME type for Unity compressed assets."""
        for suffix in ('.br', '.gz'):
            if path.endswith(suffix):
                path = path[:-len(suffix)]
        return super().guess_type(path)

    def end_headers(self):
        """Add isolation and compression metadata to every asset response."""
        self.send_header('Cross-Origin-Opener-Policy', 'same-origin')
        self.send_header('Cross-Origin-Embedder-Policy', 'require-corp')
        self.send_header('Cross-Origin-Resource-Policy', 'same-origin')
        self.send_header('Cache-Control', 'no-cache')
        path = urlsplit(self.path).path
        if path.endswith('.br'):
            self.send_header('Content-Encoding', 'br')
        elif path.endswith('.gz'):
            self.send_header('Content-Encoding', 'gzip')
        super().end_headers()


def main():
    """Parse the build path and port, then serve until interrupted; return no value."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path,
                        default=Path(__file__).resolve().parent.parent / 'AZ_demo_cesium_fixed')
    parser.add_argument('--port', type=int, default=8089)
    args = parser.parse_args()
    root = args.root.resolve()
    if not (root / 'index.html').is_file():
        parser.error(f'Unity build not found: {root / "index.html"}')
    UnityWebHandler.extensions_map['.wasm'] = 'application/wasm'
    server = ThreadingHTTPServer(('127.0.0.1', args.port),
                                 partial(UnityWebHandler, directory=str(root)))
    print(f'Open http://127.0.0.1:{args.port}/index.html', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
