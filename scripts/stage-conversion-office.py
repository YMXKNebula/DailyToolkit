"""Stage only the local Writer/Calc/Impress conversion runtime; no installer runs here."""
import fnmatch
import hashlib
import json
import pathlib
import shutil
import struct
import sys


def imports(path):
    data = path.read_bytes()
    if data[:2] != b'MZ':
        return []
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe+4] != b'PE\0\0':
        raise ValueError('Invalid PE: ' + str(path))
    count = struct.unpack_from('<H', data, pe + 6)[0]
    size = struct.unpack_from('<H', data, pe + 20)[0]
    opt = pe + 24
    is64 = struct.unpack_from('<H', data, opt)[0] == 0x20b
    directories = opt + (112 if is64 else 96)
    image_base = struct.unpack_from('<Q' if is64 else '<I', data, opt + (24 if is64 else 28))[0]
    sections = []
    for i in range(count):
        s = opt + size + 40 * i
        virtual_size, rva, raw_size, raw = struct.unpack_from('<IIII', data, s + 8)
        sections.append((rva, max(virtual_size, raw_size), raw))

    def offset(rva):
        for start, length, raw in sections:
            if start <= rva < start + length:
                return raw + rva - start
        raise ValueError('Invalid import RVA: ' + str(path))

    def name(rva):
        start = offset(rva)
        end = data.index(b'\0', start, start + 512)
        return data[start:end].decode('ascii').lower()

    result = []
    for index, width in ((1, 20), (13, 32)):
        rva, length = struct.unpack_from('<II', data, directories + index * 8)
        if not rva:
            continue
        start = offset(rva)
        for i in range(min(length // width + 1, 1024)):
            row = struct.unpack_from('<' + 'I' * (width // 4), data, start + i * width)
            if not any(row):
                break
            pointer = row[3] if index == 1 else row[1] - (0 if row[0] & 1 else image_base)
            result.append(name(pointer))
    return sorted(set(result))


def stage(source, target, crt):
    if target.exists():
        raise ValueError('Refuse to replace an existing bundle: ' + str(target))
    source_files = {str(p.relative_to(source)).replace('\\', '/'): p for p in source.rglob('*') if p.is_file()}
    excluded = [
        'cli_*', 'policy.*', 'java*', 'jvm*', 'jpipe.dll', 'jpipx.dll', 'juh*', 'officebean.dll',
        'python*', 'Engine12.dll', 'ifbclient.dll', 'hsqldb.dll', 'postgresql-*', 'libpq.dll',
        'abplo.dll', 'dbplo.dll', 'pcrlo.dll', 'clucene.dll', 'LanguageToollo.dll',
        'PresentationMinimizerlo.dll', 'update*', 'updchklo.dll', 'deploymentgui.dll',
        'so_activex.dll', 'spsupp*', 'inprocserv.dll', 'mwaw.dll', 'wpd.dll', 'wpg.dll', 'wps.dll',
        'etonyek.dll', 'pdfimportlo.dll', 'pdfiumlo.dll', 'pyuno.pyd',
    ]
    chosen = set()
    for rel in source_files:
        p = pathlib.PurePosixPath(rel)
        if p.parent.as_posix() == 'program':
            if p.suffix.lower() in ('.dll', '.ini', '.rdb') and not any(fnmatch.fnmatch(p.name.lower(), e.lower()) for e in excluded):
                chosen.add(rel)
            if p.name in ('soffice.com', 'soffice.exe', 'soffice.bin'):
                chosen.add(rel)
        elif rel.startswith(('program/types/', 'program/resource/en_US/', 'program/resource/zh_CN/',
                             'program/resource/common/fonts/', 'share/resource/common/fonts/',
                             'presets/', 'share/presets/')):
            chosen.add(rel)
        elif rel == 'program/services/services.rdb':
            chosen.add(rel)
        elif p.parent.as_posix() == 'share/registry' and p.suffix == '.xcd':
            chosen.add(rel)
        elif rel.startswith(('share/registry/res/en-US/', 'share/registry/res/zh-CN/')):
            chosen.add(rel)
        elif rel.startswith('Fonts/') or (p.parent.as_posix() == '.' and p.name in ('license.txt', 'LICENSE.html', 'NOTICE', 'CREDITS.fodt')):
            chosen.add(rel)
    for required in ('program/soffice.com', 'program/soffice.bin', 'program/mergedlo.dll', 'license.txt', 'NOTICE'):
        if required not in chosen:
            raise ValueError('Missing required upstream file: ' + required)
    if not any(rel.startswith(('presets/', 'share/presets/')) for rel in chosen):
        raise ValueError('The upstream profile presets required by desktop/app/userinstall.cxx are missing')

    # Recover any transitive dependency excluded by role above. Runtime DLLs
    # must come from this archive or the Microsoft app-local CRT, never PATH.
    program = {p.name.lower(): (rel, p) for rel, p in source_files.items() if pathlib.PurePosixPath(rel).parent.as_posix() == 'program'}
    crt_files = {p.name.lower(): p for p in crt.glob('*.dll')}
    additional = {}
    checked = set()
    system = set('activeds dbghelp httpapi mfplat mfplay mfreadwrite oledlg wer wsock32 advapi32 avrt bcrypt cabinet cfgmgr32 comctl32 comdlg32 combase credui crypt32 cryptbase cryptsp d2d1 d3d11 d3d12 d3d9 d3dcompiler_47 dcomp dhcpcsvc dnsapi dsound dwmapi dwrite dxgi dxva2 faultrep fontsub gdi32 gdiplus glu32 hid imagehlp imm32 iphlpapi kernel32 kernelbase mpr mprapi msimg32 msvcrt mswsock msxml6 ncrypt netapi32 ntdll ole32 oleacc oleaut32 opengl32 powrprof profapi propsys psapi rasapi32 rpcrt4 secur32 setupapi shcore shell32 shlwapi sspicli ucrtbase urlmon user32 userenv usp10 uxtheme version winhttp wininet winmm winscard winspool.drv wintrust wlanapi wldap32 ws2_32 wtsapi32'.split())
    system = {x if '.' in x else x + '.dll' for x in system}
    missing = set()
    while True:
        pending = {rel: source_files[rel] for rel in chosen if rel not in checked and pathlib.PurePosixPath(rel).suffix.lower() in ('.dll', '.exe', '.com', '.bin')}
        pending.update({rel: p for rel, p in additional.items() if rel not in checked})
        if not pending:
            break
        for rel, path in pending.items():
            checked.add(rel)
            for dep in imports(path):
                if dep in program:
                    chosen.add(program[dep][0])
                elif dep in crt_files:
                    additional['program/' + dep] = crt_files[dep]
                elif dep not in system and not dep.startswith(('api-ms-win-', 'ext-ms-win-')):
                    missing.add(f'{dep} imported by {rel}')
    if missing:
        raise ValueError('Unresolved dependencies:\n' + '\n'.join(sorted(missing)))
    for rel in sorted(chosen):
        dest = target / rel
        if rel.startswith('Fonts/'):
            dest = target / 'share/fonts/truetype' / pathlib.PurePosixPath(rel).name
        dest.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source_files[rel], dest)
    for rel, path in additional.items():
        shutil.copyfile(path, target / rel)
    # Source availability and notices are copied separately by the caller.
    result = dict(upstreamFiles=len(source_files), upstreamBytes=sum(p.stat().st_size for p in source_files.values()),
                  selectedFiles=len(chosen)+len(additional), selectedBytes=sum(p.stat().st_size for p in target.rglob('*') if p.is_file()),
                  microsoftRuntime=sorted(additional), checkedBinaries=len(checked), omitted=sorted(set(source_files)-chosen))
    print(json.dumps(result, ensure_ascii=False))


if __name__ == '__main__':
    stage(*(pathlib.Path(arg) for arg in sys.argv[1:4]))
