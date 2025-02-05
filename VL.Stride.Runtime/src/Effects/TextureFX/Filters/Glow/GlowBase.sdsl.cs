﻿// <auto-generated>
// Do not edit this file yourself!
//
// This code was generated by Stride Shader Mixin Code Generator.
// To generate it yourself, please install Stride.VisualStudio.Package .vsix
// and re-save the associated .sdfx.
// </auto-generated>

using System;
using Stride.Core;
using Stride.Rendering;
using Stride.Graphics;
using Stride.Shaders;
using Stride.Core.Mathematics;
using Buffer = Stride.Graphics.Buffer;

namespace Stride.Rendering
{
    public static partial class GlowBaseKeys
    {
        public static readonly ValueParameterKey<float> Amount = ParameterKeys.NewValue<float>(1.0f);
        public static readonly ValueParameterKey<float> Shape = ParameterKeys.NewValue<float>(0.0f);
        public static readonly ValueParameterKey<float> HighlightBoost = ParameterKeys.NewValue<float>(0.0f);
        public static readonly ValueParameterKey<float> Exposure = ParameterKeys.NewValue<float>(1.0f);
        public static readonly ValueParameterKey<float> Saturation = ParameterKeys.NewValue<float>(1.0f);
        public static readonly ValueParameterKey<float> AutoExposureFactor = ParameterKeys.NewValue<float>(0.0f);
        public static readonly ValueParameterKey<float> MaxRadius = ParameterKeys.NewValue<float>(1.0f);
        public static readonly ValueParameterKey<float> PreBlurWidth = ParameterKeys.NewValue<float>(8.0f);
    }
}
