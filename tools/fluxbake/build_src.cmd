@echo off
rem Build fluxbake_src.exe: fluxbake.cpp linked statically with Flux built from the Frostbite source tree
rem (FLUX_ROOT\Source: FluxFoundation, FluxCore, Flux API, embree SSE2 target, tinyobjloader, pugixml), so the
rem baker matches the source exactly (the shipped Lib\win64\Flux.dll is an older Nov 2019 build that ignores the
rem environment texture). Objects go to OBJ_ROOT (default I:\EnlightenBake\fluxsrc_obj).
setlocal enabledelayedexpansion
if "%FLUX_ROOT%"=="" set FLUX_ROOT=E:\frostbite\Code\External\EA\Flux
if "%OBJ_ROOT%"=="" set OBJ_ROOT=I:\EnlightenBake\fluxsrc_obj
call "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist bin mkdir bin
set S=%FLUX_ROOT%\Source
set E=%S%\External\embree
for %%d in (embree flux ext) do if not exist "%OBJ_ROOT%\%%d" mkdir "%OBJ_ROOT%\%%d"
set CF=/nologo /c /std:c++17 /O2 /EHsc /MD /W0 /MP /bigobj /DNDEBUG
set EMB=/DEMBREE_STATIC_LIB /DTASKING_PPL /D__SSE__ /D__SSE2__ /DEMBREE_TARGET_SSE2 /DEMBREE_LOWEST_ISA /I"%E%" /I"%E%\include" /I"%E%\common" /I"%E%\kernels"
if not exist "%OBJ_ROOT%\embree\done2.txt" (
  if not exist "%OBJ_ROOT%\embree\common_algorithms" mkdir "%OBJ_ROOT%\embree\common_algorithms"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_algorithms\\" "%E%\common\algorithms\parallel_for.cpp" "%E%\common\algorithms\parallel_for_for.cpp" "%E%\common\algorithms\parallel_for_for_prefix_sum.cpp" "%E%\common\algorithms\parallel_map.cpp" "%E%\common\algorithms\parallel_partition.cpp" "%E%\common\algorithms\parallel_prefix_sum.cpp" "%E%\common\algorithms\parallel_reduce.cpp" "%E%\common\algorithms\parallel_set.cpp" "%E%\common\algorithms\parallel_sort.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\common_lexers" mkdir "%OBJ_ROOT%\embree\common_lexers"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_lexers\\" "%E%\common\lexers\stringstream.cpp" "%E%\common\lexers\tokenstream.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\common_math" mkdir "%OBJ_ROOT%\embree\common_math"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_math\\" "%E%\common\math\constants.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\common_simd" mkdir "%OBJ_ROOT%\embree\common_simd"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_simd\\" "%E%\common\simd\sse.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\common_sys" mkdir "%OBJ_ROOT%\embree\common_sys"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_sys\\" "%E%\common\sys\alloc.cpp" "%E%\common\sys\barrier.cpp" "%E%\common\sys\condition.cpp" "%E%\common\sys\filename.cpp" "%E%\common\sys\library.cpp" "%E%\common\sys\mutex.cpp" "%E%\common\sys\regression.cpp" "%E%\common\sys\string.cpp" "%E%\common\sys\sysinfo.cpp" "%E%\common\sys\thread.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\common_tasking" mkdir "%OBJ_ROOT%\embree\common_tasking"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\common_tasking\\" "%E%\common\tasking\taskschedulerppl.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\kernels_builders" mkdir "%OBJ_ROOT%\embree\kernels_builders"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\kernels_builders\\" "%E%\kernels\builders\primrefgen.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\kernels_common" mkdir "%OBJ_ROOT%\embree\kernels_common"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\kernels_common\\" "%E%\kernels\common\acceln.cpp" "%E%\kernels\common\accelset.cpp" "%E%\kernels\common\alloc.cpp" "%E%\kernels\common\device.cpp" "%E%\kernels\common\geometry.cpp" "%E%\kernels\common\rtcore.cpp" "%E%\kernels\common\rtcore_builder.cpp" "%E%\kernels\common\scene.cpp" "%E%\kernels\common\scene_bezier_curves.cpp" "%E%\kernels\common\scene_geometry_instance.cpp" "%E%\kernels\common\scene_instance.cpp" "%E%\kernels\common\scene_line_segments.cpp" "%E%\kernels\common\scene_quad_mesh.cpp" "%E%\kernels\common\scene_triangle_mesh.cpp" "%E%\kernels\common\scene_user_geometry.cpp" "%E%\kernels\common\state.cpp" "%E%\kernels\common\stat.cpp" "%E%\kernels\common\tasksys.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\kernels_geometry" mkdir "%OBJ_ROOT%\embree\kernels_geometry"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\kernels_geometry\\" "%E%\kernels\geometry\instance_intersector.cpp" "%E%\kernels\geometry\primitive.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\kernels_subdiv" mkdir "%OBJ_ROOT%\embree\kernels_subdiv"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\kernels_subdiv\\" "%E%\kernels\subdiv\bezier_curve.cpp" "%E%\kernels\subdiv\bspline_curve.cpp" || exit /b 1
  if not exist "%OBJ_ROOT%\embree\kernels_bvh" mkdir "%OBJ_ROOT%\embree\kernels_bvh"
  cl %CF% %EMB% /Fo"%OBJ_ROOT%\embree\kernels_bvh\\" "%E%\kernels\bvh\bvh.cpp" "%E%\kernels\bvh\bvh_builder.cpp" "%E%\kernels\bvh\bvh_builder_instancing.cpp" "%E%\kernels\bvh\bvh_builder_morton.cpp" "%E%\kernels\bvh\bvh_builder_sah.cpp" "%E%\kernels\bvh\bvh_builder_twolevel.cpp" "%E%\kernels\bvh\bvh_intersector1_bvh4.cpp" "%E%\kernels\bvh\bvh_refit.cpp" "%E%\kernels\bvh\bvh_rotate.cpp" "%E%\kernels\bvh\bvh_statistics.cpp" "%E%\kernels\bvh\bvh4_factory.cpp" "%E%\kernels\bvh\bvh8_factory.cpp" || exit /b 1
  echo ok> "%OBJ_ROOT%\embree\done2.txt"
)
set FDEF=/DGLM_FORCE_DEPTH_ZERO_TO_ONE /DGLM_FORCE_NO_CTOR_INIT /DGLM_FORCE_SIZE_FUNC /DEMBREE_STATIC_LIB /DEIGEN_MPL2_ONLY /DFLUX_DLL
set FINC=/I"%FLUX_ROOT%\Include" /I"%S%" /I"%S%\External\eigen" /I"%E%\include" /I"%S%\External\glm" /I"%S%\External\pugixml\src" /I"%S%\External\stb" /I"%S%\External\tclap\include" /I"%S%\External\tinyobjloader"
if not exist "%OBJ_ROOT%\ext\done.txt" (
  cl %CF% /Fo"%OBJ_ROOT%\ext\\" "%S%\External\tinyobjloader\tiny_obj_loader.cc" "%S%\External\pugixml\src\pugixml.cpp" || exit /b 1
  echo ok> "%OBJ_ROOT%\ext\done.txt"
)
for %%d in (FluxFoundation FluxCore Flux) do (
  if not exist "%OBJ_ROOT%\flux\%%d" mkdir "%OBJ_ROOT%\flux\%%d"
  if not exist "%OBJ_ROOT%\flux\%%d\done.txt" (
    cl %CF% %FDEF% %FINC% /Fo"%OBJ_ROOT%\flux\%%d\\" "%S%\%%d\*.cpp" || exit /b 1
    echo ok> "%OBJ_ROOT%\flux\%%d\done.txt"
  )
)
if not exist "%OBJ_ROOT%\main" mkdir "%OBJ_ROOT%\main"
cl %CF% %FDEF% %FINC% /DFLUXBAKE_SRC /Fo"%OBJ_ROOT%\main\\" fluxbake.cpp || exit /b 1
dir /s /b "%OBJ_ROOT%\*.obj" > "%OBJ_ROOT%\objs.rsp"
link /nologo /OUT:bin\fluxbake_src.exe @"%OBJ_ROOT%\objs.rsp" user32.lib advapi32.lib || exit /b 1
echo built bin\fluxbake_src.exe
