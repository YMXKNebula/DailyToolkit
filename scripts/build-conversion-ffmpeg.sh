#!/usr/bin/env bash
# Cross-build the local converter, never a player, downloader or system install.
set -euo pipefail
root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)
work="$root/.local/conversion-ffmpeg"
mkdir -p "$work"
build=$(mktemp -d "$work/build-XXXXXX")
prefix="$build/prefix"
out="$build/output"
mkdir -p "$prefix" "$out/licenses" "$out/sources"
jobs=${CONVERSION_BUILD_JOBS:-2}
export CC=x86_64-w64-mingw32-gcc CXX=x86_64-w64-mingw32-g++
export AR=x86_64-w64-mingw32-ar RANLIB=x86_64-w64-mingw32-ranlib STRIP=x86_64-w64-mingw32-strip
export PKG_CONFIG_LIBDIR="$prefix/lib/pkgconfig" PKG_CONFIG_PATH="$prefix/lib/pkgconfig"
export CFLAGS='-O2' CXXFLAGS='-O2'

python3 - "$root/scripts/conversion-sources.json" "$build" <<'PY'
import hashlib,json,pathlib,subprocess,sys,tarfile
spec=pathlib.Path(sys.argv[1]); root=pathlib.Path(sys.argv[2])
for item in json.loads(spec.read_text()):
    name=item['name']; archive=root/'output'/'sources'/(name+'.tar.gz')
    subprocess.run(['curl','--fail','--silent','--show-error','--location','--connect-timeout','20','--max-time','180','--output',str(archive),item['url']],check=True)
    if hashlib.sha256(archive.read_bytes()).hexdigest()!=item['sha256']: raise RuntimeError('Source checksum mismatch: '+name)
    target=root/name; target.mkdir()
    subprocess.run(['tar','-xzf',str(archive),'--strip-components=1','-C',str(target)],check=True)
    notices=[p for p in target.iterdir() if p.is_file() and (p.name.upper().startswith(('COPYING','LICENSE','PATENT','NOTICE','AUTHORS')))]
    destination=root/'output'/'licenses'/name; destination.mkdir()
    for p in notices: (destination/p.name).write_bytes(p.read_bytes())
    if not notices: raise RuntimeError('No source license: '+name)
PY
cat > "$build/windows.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_C_COMPILER $CC)
set(CMAKE_CXX_COMPILER $CXX)
set(CMAKE_RC_COMPILER x86_64-w64-mingw32-windres)
set(CMAKE_FIND_ROOT_PATH "$prefix" /usr/x86_64-w64-mingw32)
set(CMAKE_FIND_ROOT_PATH_MODE_PROGRAM NEVER)
set(CMAKE_FIND_ROOT_PATH_MODE_LIBRARY ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_INCLUDE ONLY)
set(CMAKE_FIND_ROOT_PATH_MODE_PACKAGE ONLY)
set(CMAKE_PREFIX_PATH "$prefix")
EOF
cat > "$build/windows.meson" <<EOF
[binaries]
c = '$CC'
cpp = '$CXX'
ar = '$AR'
strip = '$STRIP'
windres = 'x86_64-w64-mingw32-windres'
pkg-config = 'pkg-config'
[host_machine]
system = 'windows'
cpu_family = 'x86_64'
cpu = 'x86_64'
endian = 'little'
EOF
run() {
    local label=$1; shift
    echo "Building $label"
    if ! "$@" >"$build/$label.log" 2>&1; then tail -n 100 "$build/$label.log"; return 1; fi
}
cmake_lib() {
    local name=$1; shift
    run "$name-configure" cmake -S "$build/$name" -B "$build/$name-build" -G Ninja \
        -DCMAKE_TOOLCHAIN_FILE="$build/windows.cmake" -DCMAKE_INSTALL_PREFIX="$prefix" \
        -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_TESTING=OFF "$@"
    run "$name-build" cmake --build "$build/$name-build" --parallel "$jobs"
    run "$name-install" cmake --install "$build/$name-build"
}
cmake_lib ogg -DINSTALL_DOCS=OFF
cmake_lib vorbis
cmake_lib opus -DOPUS_BUILD_TESTING=OFF -DOPUS_BUILD_PROGRAMS=OFF -DOPUS_DRED=OFF -DOPUS_DEEP_PLC=OFF -DOPUS_OSCE=OFF
cmake_lib webp -DWEBP_BUILD_ANIM_UTILS=OFF -DWEBP_BUILD_CWEBP=OFF -DWEBP_BUILD_DWEBP=OFF \
    -DWEBP_BUILD_GIF2WEBP=OFF -DWEBP_BUILD_IMG2WEBP=OFF -DWEBP_BUILD_VWEBP=OFF \
    -DWEBP_BUILD_WEBPINFO=OFF -DWEBP_BUILD_LIBWEBPMUX=OFF -DWEBP_BUILD_WEBPMUX=OFF -DWEBP_BUILD_EXTRAS=OFF
run dav1d-configure meson setup "$build/dav1d-build" "$build/dav1d" --cross-file "$build/windows.meson" \
    --prefix "$prefix" --libdir lib --buildtype release --default-library static -Denable_tools=false -Denable_tests=false
run dav1d-build ninja -C "$build/dav1d-build" -j "$jobs"
run dav1d-install meson install -C "$build/dav1d-build"
(
    cd "$build/lame"
    run lame-configure ./configure --host=x86_64-w64-mingw32 --prefix="$prefix" --disable-shared --enable-static --disable-frontend --disable-decoder --disable-nasm
    run lame-build make -j "$jobs"
    run lame-install make install
)
(
    cd "$build/x264"
    run x264-configure ./configure --host=x86_64-w64-mingw32 --cross-prefix=x86_64-w64-mingw32- --prefix="$prefix" \
        --enable-static --disable-cli --disable-opencl --disable-avs --disable-lavf --disable-swscale --disable-ffms
    run x264-build make -j "$jobs"
    run x264-install make install-lib-static install-lib-dev
)
(
    cd "$build/vpx"
    run vpx-configure ./configure --target=x86_64-win64-gcc --prefix="$prefix" --as=nasm \
        --enable-static --disable-shared --disable-examples --disable-tools --disable-docs --disable-unit-tests \
        --disable-vp8-encoder --disable-vp8-decoder --disable-vp9-decoder
    run vpx-build make -j "$jobs"
    run vpx-install make install
)
(
    cd "$build/ffmpeg"
    # Keep decoding broad within the advertised containers; only encode our
    # exposed targets. There is no network protocol, capture device or AI filter.
    decoders='aac,aac_fixed,ac3,eac3,alac,mp1,mp1float,mp2,mp2float,mp3,mp3float,flac,vorbis,opus,wmav1,wmav2,wmapro,wmalossless,pcm_s16le,pcm_s16be,pcm_s24le,pcm_s24be,pcm_s32le,pcm_s32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_u8,pcm_alaw,pcm_mulaw,adpcm_ms,adpcm_ima_wav,h264,hevc,mpeg1video,mpeg2video,mpeg4,msmpeg4v1,msmpeg4v2,msmpeg4v3,wmv1,wmv2,wmv3,vc1,vp8,vp9,libdav1d,prores,ffv1,dvvideo,mjpeg,png,bmp,tiff,gif,webp,rawvideo,qtrle'
    run ffmpeg-configure ./configure --target-os=mingw32 --arch=x86_64 --enable-cross-compile \
        --cross-prefix=x86_64-w64-mingw32- --cc="$CC" --cxx="$CXX" --pkg-config=pkg-config --pkg-config-flags=--static \
        --extra-cflags="-I$prefix/include" --extra-ldflags="-L$prefix/lib -static" \
        --enable-static --disable-shared --enable-gpl --disable-autodetect --disable-debug --disable-doc \
        --disable-network --disable-devices --disable-hwaccels --disable-avdevice --disable-everything \
        --enable-ffmpeg --disable-ffplay --disable-ffprobe \
        --enable-libx264 --enable-libmp3lame --enable-libvorbis --enable-libopus --enable-libvpx --enable-libwebp --enable-libdav1d \
        --enable-protocol=file,pipe --enable-decoder="$decoders" \
        --enable-encoder=libmp3lame,aac,pcm_s16le,flac,libvorbis,libvpx_vp9,libopus,libx264,wmv2,wmav2,png,libwebp \
        --enable-demuxer=aac,mov,matroska,avi,asf,mpegps,mpegts,mp3,wav,flac,ogg,image2,image2pipe,png_pipe,webp_pipe,jpeg_pipe,bmp_pipe,tiff_pipe,gif,rawvideo \
        --enable-muxer=image2,image2pipe,mp3,ipod,wav,flac,ogg,mp4,asf,matroska,webm,null \
        --enable-parser=aac,ac3,flac,h264,hevc,mjpeg,mpeg4video,mpegaudio,mpegvideo,opus,vorbis,vp8,vp9,av1,png \
        --enable-bsf=aac_adtstoasc,h264_mp4toannexb,hevc_mp4toannexb,extract_extradata \
        --enable-filter=pad,format,scale,aresample,aformat,null,anull,trim,atrim,setpts,asetpts,fps
    run ffmpeg-build make -j "$jobs" ffmpeg.exe
    "$STRIP" ffmpeg.exe
    cp ffmpeg.exe "$out/ffmpeg.exe"
    cp config.h config_components.h ffbuild/config.mak "$out/sources/"
    cp COPYING.GPLv2 "$out/LICENSE.txt"
)
# Full preferred source and exact build recipe travel with the executable.
cp "$root/scripts/build-conversion-ffmpeg.sh" "$root/scripts/conversion-sources.json" "$out/sources/"
printf '%s\n' 'FFmpeg 9.0.2, GPL-2.0-or-later, with x264 and the codecs listed in sources/conversion-sources.json.' \
    'Unmodified upstream source archives, notices and the cross-build recipe are included in sources/ and licenses/.' \
    'No ffplay, ffprobe, network protocols, capture devices, neural audio options or AI filters are built.' \
    'Build dependencies on Ubuntu: gcc-mingw-w64-x86-64-posix g++-mingw-w64-x86-64-posix nasm cmake ninja-build meson pkg-config make curl python3.' \
    'Rebuild from the DailyToolkit repository with: bash scripts/build-conversion-ffmpeg.sh' > "$out/SOURCE.txt"
x86_64-w64-mingw32-objdump -p "$out/ffmpeg.exe" | sed -n 's/.*DLL Name: //p' > "$out/system-dependencies.txt"
if grep -Eiq 'libgcc|libstdc|libwinpthread|libav|\.dll[^[:space:]]' "$out/system-dependencies.txt"; then
    echo 'Unexpected external runtime DLL'; cat "$out/system-dependencies.txt"; exit 1
fi
mkdir -p "$root/.local/conversion-tools/tools"
if [[ -e "$root/.local/conversion-tools/tools/ffmpeg" ]]; then echo 'Refuse to replace an existing engine directory'; exit 1; fi
mv "$out" "$root/.local/conversion-tools/tools/ffmpeg"
echo 'FFmpeg bundle ready:'
du -sh "$root/.local/conversion-tools/tools/ffmpeg"
cat "$root/.local/conversion-tools/tools/ffmpeg/system-dependencies.txt"
