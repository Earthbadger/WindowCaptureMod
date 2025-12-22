cmake_minimum_required(VERSION 3.15)

# Define the project
project(SpoutWGCSender LANGUAGES CXX)

# We need C++17 for C++/WinRT
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# --- IMPORTANT ---
# Tell CMake where to find the Spout SDK we just built and installed.
# Replace the path below with the actual path to YOUR Spout install directory.
set(Spout2_DIR "c:/Users/Earth/Downloads/Spout2-master/INSTALL/lib/cmake/spout2")

# Find the Spout2 package using the path we just set
find_package(Spout2 REQUIRED)

# Add our executable target from our source file
add_executable(SpoutWGCSender SpoutWGCSender.cpp)

# This is required for C++/WinRT to handle large object files
target_compile_options(SpoutWGCSender PRIVATE /bigobj)

# Link our executable against the SpoutDX library and the required
# Windows system libraries for graphics capture.
target_link_libraries(SpoutWGCSender PRIVATE
    Spout2::SpoutDX
    d3d11
    dxgi
    dwmapi
    windowsapp
    CoreMessaging
    Psapi
)

# Add Spout source directories to include path to ensure headers are found
target_include_directories(SpoutWGCSender PRIVATE
    "c:/Users/Earth/Downloads/Spout2-master/SPOUTSDK/SpoutDirectX/SpoutDX"
    "c:/Users/Earth/Downloads/Spout2-master/SPOUTSDK/SpoutGL"
)

# Add a post-build command to automatically copy the SpoutDX.dll
# to the executable's directory after a successful build.
add_custom_command(TARGET SpoutWGCSender POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${Spout2_DIR}/../../../bin/SpoutDX.dll"
    $<TARGET_FILE_DIR:SpoutWGCSender>
    COMMENT "Copying SpoutDX.dll to output directory"
)

# Statically link the C++ runtime for Release builds to avoid
# needing the VC++ Redistributable on the user's machine.
# This makes the executable more portable.
if(MSVC)
  target_compile_options(SpoutWGCSender PRIVATE
      $<$<CONFIG:Release>:/MT>
      $<$<CONFIG:Debug>:/MTd>
  )
endif()

# --- Installation / Packaging ---
# Set the install prefix to a local directory inside the build folder
# to avoid permission errors when writing to system directories.
set(CMAKE_INSTALL_PREFIX "${CMAKE_CURRENT_BINARY_DIR}/install")

include(GNUInstallDirs)
install(TARGETS SpoutWGCSender RUNTIME DESTINATION bin)
install(FILES "${Spout2_DIR}/../../../bin/SpoutDX.dll" DESTINATION bin)