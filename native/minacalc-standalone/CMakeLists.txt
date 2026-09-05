cmake_minimum_required(VERSION 3.15)
project(minacalc_standalone CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release)
endif()

# The upstream MinaCalc source already has a STANDALONE_CALC guard that
# strips out every include/call into the rest of the Etterna engine
# (RageFileManager, XML param dumping, lua registration, etc.), which is
# exactly what lets this be a single self-contained binary.
add_compile_definitions(STANDALONE_CALC)

if(MSVC)
  add_compile_options(/O2 /EHsc /W3)
  add_compile_definitions(_CRT_SECURE_NO_WARNINGS NOMINMAX)
else()
  add_compile_options(-O2 -Wall -Wextra -Wno-unused-parameter)
endif()

file(GLOB MINACALC_SOURCES
  "${CMAKE_CURRENT_SOURCE_DIR}/src/Etterna/MinaCalc/*.cpp"
)

add_executable(msd
  src/main.cpp
  ${MINACALC_SOURCES}
)

target_include_directories(msd PRIVATE
  ${CMAKE_CURRENT_SOURCE_DIR}/src
  ${CMAKE_CURRENT_SOURCE_DIR}/src/Etterna/MinaCalc
)

# Static-link the runtime where possible so the resulting binary doesn't
# need matching MinGW/libstdc++ DLLs (Windows) or a specific glibc++
# (Linux) sitting next to it on the target machine.
if(WIN32)
  target_link_options(msd PRIVATE -static -static-libgcc -static-libstdc++)
elseif(UNIX AND NOT APPLE)
  target_link_options(msd PRIVATE -static-libgcc -static-libstdc++)
endif()

install(TARGETS msd RUNTIME DESTINATION .)
