# CMakeLists.txt
cmake_minimum_required(VERSION 3.10)

project(GraphicsCaptureCpp)

# Set C++ standard to C++17, which is required by C++/WinRT
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# Add the executable target from main.cpp
add_executable(GraphicsCapture main.cpp)

set_target_properties(GraphicsCapture PROPERTIES OUTPUT_NAME "GraphicsCapture")

# Link against the required libraries for DirectX and Windows Runtime
target_link_libraries(GraphicsCapture PRIVATE d3d11.lib dxgi.lib dwmapi.lib windowsapp user32)

# This helps Visual Studio find headers correctly
target_include_directories(GraphicsCapture PRIVATE ${CMAKE_CURRENT_SOURCE_DIR})

# --- Add the new RenderPlugin DLL target ---
add_library(RenderPlugin SHARED RenderPlugin.cpp)

# Add the include path for Unity's native plugin API
# !!! YOU MAY NEED TO CHANGE THIS PATH TO MATCH YOUR UNITY INSTALLATION !!!
# Define the path in a variable to handle spaces and slashes correctly
set(UNITY_PLUGIN_API_PATH "C:/Program Files/Unity/Editor/Data/PluginAPI")

target_include_directories(RenderPlugin PRIVATE "${UNITY_PLUGIN_API_PATH}")
target_link_libraries(RenderPlugin PRIVATE 
    d3d11.lib
    dxgi.lib
    windowsapp.lib # For WinRT
    user32.lib
)

# --- Set Output Directories ---
set_target_properties(GraphicsCapture PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/$<CONFIG>")
set_target_properties(RenderPlugin PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/$<CONFIG>")
