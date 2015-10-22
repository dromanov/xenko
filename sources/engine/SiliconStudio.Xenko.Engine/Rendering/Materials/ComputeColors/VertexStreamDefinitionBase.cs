// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.
using SiliconStudio.Core;

namespace SiliconStudio.Xenko.Rendering.Materials.ComputeColors
{
    /// <summary>
    /// Base implementation for <see cref="IVertexStreamDefinition"/>
    /// </summary>
    [DataContract(Inherited = true)]
    public abstract class VertexStreamDefinitionBase :  IVertexStreamDefinition
    {
        public abstract string GetSemanticName();
    }
}