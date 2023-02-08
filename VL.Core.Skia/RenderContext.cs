﻿using SkiaSharp;
using System;
using SharpDX.Direct3D11;
using VL.Skia.Egl;

namespace VL.Skia
{
    public sealed class RenderContext : RefCounted
    {
        public const int ResourceCacheLimit = 512 * 1024 * 1024;

        // Little helper class to be able to access thread local memory from different thread
        sealed class Ref<T>
        {
            public T Value;
        }

        [ThreadStatic]
        private static Ref<RenderContext> s_threadContext;

        /// <summary>
        /// Returns the render context for the current thread.
        /// </summary>
        /// <returns>The render context for the current thread.</returns>
        public static RenderContext ForCurrentThread()
        {
            // Keep a render context per thread
            var rootRef = s_threadContext ??= new Ref<RenderContext>();

            var context = rootRef.Value;
            if (context != null)
            {
                context.AddRef();
                return context;
            }

            try
            {
                // Setup the device manually to ensure we have one for each thread
                using var device = CreateD3D11Device();
                var angleDevice = EglDevice.FromD3D11(device.NativePointer);
                context = New(angleDevice, 0);
            }
            catch
            {
                // Let EGL select the device. The problem here is that the underlying D3D11 device is the same for all threads which can lead to hard to track crashes.
                // See https://registry.khronos.org/EGL/sdk/docs/man/html/eglGetPlatformDisplay.xhtml
                context = New(EglDisplay.GetPlatformDefault(), 0);
            }

            // The context might get disposed in a different thread (happend on some preview windows of camera devices)
            // Therefor store the "reference" so we can set it to null once the ref count goes to zero
            context.ThreadLocalStorage = rootRef;

            return rootRef.Value = context;

            static Device CreateD3D11Device()
            {
                try
                {
                    return new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.None);
                }
                catch
                {
                    return new Device(SharpDX.Direct3D.DriverType.Warp, DeviceCreationFlags.None);
                }
            }
        }

        public static RenderContext New(EglDevice device, int msaaSamples)
        {
            var display = EglDisplay.FromDevice(device);
            return New(display, msaaSamples);
        }

        public static RenderContext New(EglDisplay display, int msaaSamples)
        {
            var context = EglContext.New(display, msaaSamples);
            context.MakeCurrent(default);
            var skiaContext = GRContext.CreateGl(GRGlInterface.CreateAngle());
            if (skiaContext is null)
                throw new Exception("Failed to create Skia backend graphics context");

            // 512MB instead of the default 96MB
            skiaContext.SetResourceCacheLimit(ResourceCacheLimit);
            return new RenderContext(context, skiaContext);
        }

        public readonly EglContext EglContext;
        public readonly GRContext SkiaContext;

        RenderContext(EglContext eglContext, GRContext skiaContext)
        {
            EglContext = eglContext ?? throw new ArgumentNullException(nameof(eglContext));
            SkiaContext = skiaContext ?? throw new ArgumentNullException(nameof(skiaContext));
        }

        ~RenderContext()
        {
            Destroy();
        }

        Ref<RenderContext> ThreadLocalStorage { get; set; }

        protected override void Destroy()
        {
            GC.SuppressFinalize(this);

            MakeCurrent();

            SkiaContext.Dispose();
            EglContext.Dispose();

            // Set the reference to null so a new context can be created if needed
            if (ThreadLocalStorage != null)
                ThreadLocalStorage.Value = null;
        }

        public void MakeCurrent(EglSurface surface = default)
        {
            EglContext.MakeCurrent(surface);
        }
    }
}
