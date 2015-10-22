﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using SiliconStudio.Core.Diagnostics;
using SiliconStudio.Xenko.Rendering;
using SiliconStudio.Xenko.Graphics;

namespace SiliconStudio.Xenko.Shaders.Compiler
{
    internal class ShaderBytecodeResult : LoggerResult
    {
        public ShaderBytecode Bytecode { get; set; }
    }

    internal interface IShaderCompiler
    {
        ShaderBytecodeResult Compile(string shaderSource, string entryPoint, ShaderStage stage, ShaderMixinParameters compilerParameters, EffectReflection reflection, string sourceFilename = null);
    }
}