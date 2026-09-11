namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of the native plugin's graphics binding: whether the
    /// Unity plugin entry point has run, and whether the plugin currently holds
    /// the D3D11 device Unity is using.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These two facts say that Unity's renderer was D3D11 at a device event
    /// and that a device was obtained and is held. They say nothing about the
    /// adapter's vendor, its driver model, NVENC support, asynchronous encode,
    /// device health, or whether an encoder session would initialize - the
    /// capability snapshot and the real session answer those.
    /// </para>
    /// <para>
    /// No device address, handle, or native pointer is carried here. A default
    /// value is an uninitialized snapshot, which is what a platform that cannot
    /// be observed returns.
    /// </para>
    /// </remarks>
    internal readonly struct NvencGraphicsObservationV1
    {
        private readonly bool _initialized;

        internal NvencGraphicsObservationV1(
            bool isUnityPluginLoaded, bool hasCurrentD3D11Device)
        {
            IsUnityPluginLoaded = isUnityPluginLoaded;
            HasCurrentD3D11Device = hasCurrentD3D11Device;
            _initialized = true;
        }

        internal bool IsInitialized => _initialized;

        internal bool IsUnityPluginLoaded { get; }

        internal bool HasCurrentD3D11Device { get; }
    }
}
