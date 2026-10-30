#!/usr/bin/env python3
"""Preview or delete trailers in a dedicated download folder. Stop trailer tasks first."""
import argparse
import hashlib
import os
from pathlib import Path


def cleanup_files(folder, plugin_data=None):
    folder = Path(os.path.abspath(folder))
    if not folder.is_dir() or folder == Path(folder.anchor):
        raise ValueError("Specify an existing, dedicated trailer download folder (not a filesystem root).")
    videos = sorted(p for p in folder.iterdir()
                    if p.is_file() and not p.is_symlink() and p.suffix.lower() == '.mp4'
                    and not p.name.startswith('._'))
    backups, sidecars = [], []
    for video in videos:
        sidecar = video.with_suffix('.json')
        if sidecar.is_file() and not sidecar.is_symlink():
            sidecars.append(sidecar)
        if plugin_data:
            key = hashlib.sha256(str(video).encode('utf-8')).hexdigest().upper()
            for subfolder, suffix in [('trailer-audio-originals', '.mp4'),
                                      ('trailer-audio-originals-state', '.json')]:
                candidate = Path(plugin_data) / subfolder / (key + suffix)
                if candidate.is_file() and not candidate.is_symlink():
                    backups.append(candidate)
    # Check backup permissions before removing any playback files.
    return backups + sidecars + videos


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--folder', required=True, help='Dedicated trailer download folder; subfolders are not traversed')
    parser.add_argument('--plugin-data', help='Plugin data directory containing trailer-audio-originals')
    parser.add_argument('--delete', action='store_true', help='Actually delete the previewed files; default is preview only')
    args = parser.parse_args()
    try:
        files = cleanup_files(args.folder, args.plugin_data)
        snapshots = [(p, p.stat()) for p in files]
        for path, _ in snapshots:
            print(path)
        total = sum(info.st_size for _, info in snapshots)
        print('{} files; {:.1f} MiB'.format(len(files), total / 1024.0 ** 2))
        if not args.delete:
            print('PREVIEW ONLY: nothing deleted. Stop trailer download/reprocess tasks and playback before rerunning with --delete.')
            return
        for path, before in snapshots:
            now = path.stat()
            if path.is_symlink() or (now.st_ino, now.st_size, now.st_mtime_ns) != (before.st_ino, before.st_size, before.st_mtime_ns):
                raise ValueError('File changed since preview; stop trailer tasks before deleting: ' + str(path))
        for path, _ in snapshots:
            path.unlink()
        print('Deleted the listed files. Scan the trailer library, then run Download TMDB Trailers to download fresh files.')
    except (OSError, ValueError) as ex:
        parser.exit(1, 'Cleanup stopped: {}\n'.format(ex))


if __name__ == '__main__':
    main()
