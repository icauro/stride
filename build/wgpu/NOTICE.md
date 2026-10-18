# WebGPU binding provenance

The generator uses CppAst 0.22.0 to translate the native C declarations, following
the approach illustrated by Amer Koleci's MIT-licensed Alimer.Bindings generator:
https://github.com/amerkoleci/Alimer.Bindings/tree/b2daffe26d57fbef24553da901150467e3ecb333/src/Generator

The emitter here is specific to WebGPU and preserves C names. It generates raw
unmanaged function pointers, explicit C calling conventions, native-width handles,
64-bit flag typedefs, the anonymous display-handle union, and descriptor defaults.
It does not depend on the Alimer or Silk.NET runtime packages.

Headers are copied unmodified from the wgpu-native v29.0.1.1 Windows x64 MSVC
release archive. The manifest records the archive/header SHA-256 hashes and source
commit. The standard header's upstream revision is
673658bc2bd70ec39fc55ebe6bb0173cf6d0a603.

Licenses:

- webgpu.h: BSD-3-Clause, in headers/webgpu-headers-LICENSE.
- wgpu.h / wgpu-native: MIT or Apache-2.0, in headers/wgpu-LICENSE.MIT and
  headers/wgpu-LICENSE.APACHE.
- CppAst and its dependencies retain their package licenses through NuGet.
