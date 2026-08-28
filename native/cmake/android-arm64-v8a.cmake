# Reference entry point for future Android builds.  Pass the NDK's official
# toolchain file to CMake and select this ABI; this project deliberately does
# not bundle an Android SDK or NDK.
#
# cmake -S native -B native/out/android-arm64-v8a -G Ninja \
#   -DCMAKE_TOOLCHAIN_FILE="$ANDROID_NDK_HOME/build/cmake/android.toolchain.cmake" \
#   -DCMAKE_ANDROID_ARCH_ABI=arm64-v8a -DCMAKE_BUILD_TYPE=Release
set(HARUCHAT_ANDROID_ABI "arm64-v8a" CACHE STRING "Prepared Android ABI")
