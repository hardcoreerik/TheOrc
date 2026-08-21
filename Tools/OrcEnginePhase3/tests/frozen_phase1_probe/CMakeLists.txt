# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
cmake_minimum_required(VERSION 3.20)
project(OrcEngineFrozenPhase1Probe CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT ORCENGINE_PHASE1_SOURCE_DIR)
    message(FATAL_ERROR "ORCENGINE_PHASE1_SOURCE_DIR is required")
endif()
add_subdirectory("${ORCENGINE_PHASE1_SOURCE_DIR}" phase1)
add_executable(orcengine_phase1_snapshot_frozen ../../tools/phase1_snapshot.cpp)
target_link_libraries(orcengine_phase1_snapshot_frozen PRIVATE orcengine_phase1)
