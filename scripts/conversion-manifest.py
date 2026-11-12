"""Create/verify the exact prepared component inventory, excluding the manifest itself."""
import hashlib
import json
import pathlib
import sys


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inventory(root):
    files = []
    # Path ordering follows the host OS (case-insensitive on Windows). Use the
    # archive's exact relative strings on both builders and Windows verification.
    for path in sorted(root.rglob('*'), key=lambda p: p.relative_to(root).as_posix()):
        if path.is_symlink():
            raise ValueError('A bundle cannot include links: ' + str(path))
        if path.is_file() and path != root / 'bundle.json':
            rel = path.relative_to(root).as_posix()
            if any(part.startswith('.') for part in pathlib.PurePosixPath(rel).parts):
                raise ValueError('A bundle cannot include hidden development files: ' + rel)
            files.append(dict(path=rel, bytes=path.stat().st_size, sha256=digest(path)))
    return files


if __name__ == '__main__':
    mode, directory, component, *recipes = sys.argv[1:]
    root = pathlib.Path(directory)
    manifest = root / 'bundle.json'
    recipe = {pathlib.Path(p).name: digest(pathlib.Path(p)) for p in recipes}
    value = dict(schema=1, component=component, recipe=recipe, files=inventory(root))
    if mode == 'create':
        if manifest.exists():
            raise ValueError('Refuse to replace a component manifest')
        manifest.write_text(json.dumps(value, indent=2) + '\n', encoding='utf-8')
    elif mode == 'verify':
        expected = json.loads(manifest.read_text(encoding='utf-8'))
        if value != expected:
            raise ValueError('Prepared component differs from its pinned recipe or inventory: ' + component)
    else:
        raise ValueError('Expected create or verify')
