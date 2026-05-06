cmake_minimum_required(VERSION 3.20)

project(CrashlessLLMCore VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(CRASHLESS_BUILD_SHARED "Build crashless_core as a shared library" ON)
set(CRASHLESS_LLAMA_CPP_DIR "" CACHE PATH "Path to a llama.cpp source checkout")

if(CRASHLESS_LLAMA_CPP_DIR)
    add_subdirectory("${CRASHLESS_LLAMA_CPP_DIR}" "${CMAKE_BINARY_DIR}/llama.cpp")
elseif(EXISTS "${CMAKE_CURRENT_SOURCE_DIR}/llama.cpp/CMakeLists.txt")
    add_subdirectory("${CMAKE_CURRENT_SOURCE_DIR}/llama.cpp" "${CMAKE_BINARY_DIR}/llama.cpp")
else()
    find_package(llama CONFIG REQUIRED)
endif()

find_package(Threads REQUIRED)

if(TARGET llama)
    set(CRASHLESS_LLAMA_TARGET llama)
elseif(TARGET llama::llama)
    set(CRASHLESS_LLAMA_TARGET llama::llama)
else()
    message(FATAL_ERROR "Could not locate the llama.cpp CMake target (expected llama or llama::llama).")
endif()

add_library(crashless_core SHARED
    crashless_core.cpp
    crashless_core.h
)

add_library(CrashlessLLM::crashless_core ALIAS crashless_core)

target_compile_definitions(crashless_core
    PRIVATE
        CRASHLESS_CORE_BUILD
)

target_include_directories(crashless_core
    PUBLIC
        "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}>"
        "$<INSTALL_INTERFACE:include>"
)

target_link_libraries(crashless_core
    PRIVATE
        ${CRASHLESS_LLAMA_TARGET}
        Threads::Threads
)

if(MSVC)
    target_compile_options(crashless_core PRIVATE /W4 /permissive-)
else()
    target_compile_options(crashless_core PRIVATE -Wall -Wextra -Wpedantic)
endif()

set_target_properties(crashless_core PROPERTIES
    OUTPUT_NAME "crashless_core"
    POSITION_INDEPENDENT_CODE ON
    CXX_VISIBILITY_PRESET hidden
    VISIBILITY_INLINES_HIDDEN YES
)

install(TARGETS crashless_core
    RUNTIME DESTINATION bin
    LIBRARY DESTINATION lib
    ARCHIVE DESTINATION lib
)

install(FILES crashless_core.h DESTINATION include)
