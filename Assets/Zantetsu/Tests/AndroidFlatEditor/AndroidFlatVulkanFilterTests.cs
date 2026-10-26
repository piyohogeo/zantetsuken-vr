// Exercise migration of deprecated PlayerSettings filter lists as an intentional compatibility contract.
#pragma warning disable CS0618
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.EditorTools.AndroidFlat;
using Object = UnityEngine.Object;

namespace Zantetsu.AndroidFlat.Tests
{
    public sealed class AndroidFlatVulkanFilterTests
    {
        [Test]
        public void KeepsThePinnedEngineDenyDefaultsWhenNoCustomFiltersExist()
        {
            VulkanDeviceFilterLists phone = CreatePhone(null, null, null);
            try
            {
                // The native constructor returns empty arrays. The build must explicitly retain this engine's
                // compatibility rule; comparing two new empty assets would falsely approve dropping the default Deny.
                Assert.That(phone.vulkanDeviceDenyFilters.Length, Is.EqualTo(1));
                VulkanDeviceFilterData denied = phone.vulkanDeviceDenyFilters[0];
                Assert.That(denied.brandName, Is.EqualTo("((G|g)oogle)"));
                Assert.That(denied.productName, Is.EqualTo("(oriole|raven|bluejay)"));
                foreach (string brand in new[] { "google", "Google" })
                    foreach (string product in new[] { "oriole", "raven", "bluejay" })
                        Assert.That(Matches(denied, brand, product), Is.True, brand + "/" + product);
                Assert.That(Matches(denied, "samsung", "raven"), Is.False);
                Assert.That(phone.vulkanDeviceAllowFilters.Length, Is.EqualTo(1));
                foreach (string product in new[] { "oriole", "raven", "bluejay" })
                    Assert.That(Matches(phone.vulkanDeviceAllowFilters[0], "google", product), Is.True, product);
                Assert.DoesNotThrow(phone.EnsureValidOrThrow);
            }
            finally { Destroy(phone); }
        }

        [Test]
        public void ClonesACustomSourceAssetWithoutChangingItOrUsingLegacyLists()
        {
            string path = AssetDatabase.GenerateUniqueAssetPath("Assets/AndroidFlatVulkanFilterSourceTests.vulkandevicefilter");
            VulkanDeviceFilterLists source = NewCustomSource();
            VulkanDeviceFilterLists phone = null;
            try
            {
                AssetDatabase.CreateAsset(source, path);
                AssetDatabase.SaveAssets();
                byte[] bytes = File.ReadAllBytes(path);
                string json = EditorJsonUtility.ToJson(source);
                var allow = source.vulkanDeviceAllowFilters;
                var deny = source.vulkanDeviceDenyFilters;
                var jobs = source.vulkanGraphicsJobsDeviceFilters;
                phone = CreatePhone(source, new[] { LegacyFilter("IgnoredAllow") }, new[] { LegacyFilter("IgnoredDeny") });
                Assert.That(phone, Is.Not.SameAs(source));
                Assert.That(phone.vulkanDeviceAllowFilters.Take(allow.Length), Is.EqualTo(allow));
                Assert.That(phone.vulkanDeviceAllowFilters.Length, Is.EqualTo(allow.Length + 1));
                Assert.That(phone.vulkanDeviceDenyFilters, Is.EqualTo(deny));
                Assert.That(phone.vulkanGraphicsJobsDeviceFilters, Is.EqualTo(jobs));
                phone.vulkanDeviceDenyFilters = new[] { Filter("ChangedClone") };
                Assert.That(EditorJsonUtility.ToJson(source), Is.EqualTo(json));
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
            }
            finally { Destroy(phone); if (!AssetDatabase.DeleteAsset(path)) Destroy(source); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PreservesBothLegacyListsWhenNoNonemptySourceOverridesThem(bool emptyAsset)
        {
            VulkanDeviceFilterLists source = emptyAsset ? new VulkanDeviceFilterLists("Empty test source") : null;
            VulkanDeviceFilterLists phone = null;
            var legacyAllow = new[] { LegacyFilter("LegacyAllow") };
            var legacyDeny = new[] { LegacyFilter("LegacyDeny") };
            try
            {
                if (source != null)
                {
                    source.vulkanDeviceAllowFilters = Array.Empty<VulkanDeviceFilterData>();
                    source.vulkanDeviceDenyFilters = Array.Empty<VulkanDeviceFilterData>();
                }
                phone = CreatePhone(source, legacyAllow, legacyDeny);
                Assert.That(phone.vulkanDeviceAllowFilters.Length, Is.EqualTo(2));
                AssertFilter(phone.vulkanDeviceAllowFilters[0], legacyAllow[0]);
                Assert.That(phone.vulkanDeviceDenyFilters.Length, Is.EqualTo(1));
                AssertFilter(phone.vulkanDeviceDenyFilters[0], legacyDeny[0]);
                Assert.That(legacyAllow[0].brandName, Is.EqualTo("^LegacyAllowBrand$"));
                Assert.That(legacyDeny[0].brandName, Is.EqualTo("^LegacyDenyBrand$"));
            }
            finally { Destroy(phone); Destroy(source); }
        }

        [Test]
        public void AddsOnlyTheExactGooglePixel6FamilyException()
        {
            VulkanDeviceFilterLists phone = CreatePhone(null, Array.Empty<AndroidDeviceFilterData>(), Array.Empty<AndroidDeviceFilterData>());
            try
            {
                VulkanDeviceFilterData exception = phone.vulkanDeviceAllowFilters.Last();
                foreach (string brand in new[] { "google", "Google" })
                    foreach (string product in new[] { "oriole", "raven", "bluejay" })
                        Assert.That(Matches(exception, brand, product), Is.True, brand + "/" + product);
                foreach (var device in new[] {
                    ("google", "oriole_pro"), ("google", "prefix_oriole"), ("google", "bluejay_pro"),
                    ("google", "prefix_bluejay"), ("google", "raven_pro"),
                    ("google", "prefix_raven"), ("google", "Raven"), ("samsung", "raven"),
                    ("samsung", "oriole"), ("samsung", "bluejay"), ("google", "panther"), ("google", "cheetah"),
                    ("google", "Oriole"), ("google", "Bluejay"),
                    ("fakegoogle", "raven"), ("Google Inc", "raven"), ("", "raven"), ("google", "") })
                    Assert.That(Matches(exception, device.Item1, device.Item2), Is.False, device.ToString());
                Assert.That(string.IsNullOrEmpty(exception.vendorName), Is.True);
                Assert.That(string.IsNullOrEmpty(exception.deviceName), Is.True);
                Assert.That(string.IsNullOrEmpty(exception.androidOsVersionString), Is.True);
                Assert.That(string.IsNullOrEmpty(exception.vulkanApiVersionString), Is.True);
                Assert.That(string.IsNullOrEmpty(exception.driverVersionString), Is.True);
            }
            finally { Destroy(phone); }
        }

        [Test]
        public void RealBuildScopeRestoresFilterReferenceAndSettingsBytesAndDeletesItsTemporaryAsset()
        {
            VulkanDeviceFilterLists original = PlayerSettings.Android.androidVulkanDeviceFilterListAsset;
            var originalBytes = SnapshotSettings();
            var shaderPassCounts = new Dictionary<string, int> {
                { "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectUnlit.shader", 2 },
                { "Assets/Zantetsu/Runtime/Rendering/VpIndexedIndirectShadowCaster.shader", 1 },
                { "Assets/Zantetsu/Runtime/Rendering/VpStencilVolume.shader", 1 }
            };
            var shaderBytes = shaderPassCounts.Keys.ToDictionary(path => path, File.ReadAllBytes);
            string sourcePath = AssetDatabase.GenerateUniqueAssetPath("Assets/AndroidFlatVulkanScopeSourceTests.vulkandevicefilter");
            VulkanDeviceFilterLists source = NewCustomSource();
            IDisposable scope = null;
            string temporaryPath = null;
            try
            {
                AssetDatabase.CreateAsset(source, sourcePath);
                PlayerSettings.Android.androidVulkanDeviceFilterListAsset = source;
                AssetDatabase.SaveAssets();
                var baseline = SnapshotSettings();
                byte[] sourceBytes = File.ReadAllBytes(sourcePath);
                Type scopeType = typeof(AndroidFlatBuild).GetNestedType("BuildSettingsScope", BindingFlags.NonPublic);
                Assert.That(scopeType, Is.Not.Null);
                scope = (IDisposable)Activator.CreateInstance(scopeType, true);
                try
                {
                    VulkanDeviceFilterLists active = PlayerSettings.Android.androidVulkanDeviceFilterListAsset;
                    Assert.That(active, Is.Not.Null.And.Not.SameAs(source));
                    temporaryPath = AssetDatabase.GetAssetPath(active);
                    Assert.That(temporaryPath, Does.StartWith("Assets/AndroidFlatBuild"));
                    Assert.That(temporaryPath, Does.EndWith(".vulkandevicefilter"));
                    Assert.That(File.Exists(temporaryPath), Is.True);
                    Assert.That(File.Exists(temporaryPath + ".meta"), Is.True);
                    Assert.That(active.vulkanDeviceDenyFilters, Is.EqualTo(source.vulkanDeviceDenyFilters));
                    foreach (string product in new[] { "oriole", "raven", "bluejay" })
                        Assert.That(Matches(active.vulkanDeviceAllowFilters.Last(), "google", product), Is.True, product);
                    foreach (var shader in shaderPassCounts)
                    {
                        Assert.That(File.ReadAllBytes(shader.Key), Is.Not.EqualTo(shaderBytes[shader.Key]),
                            shader.Key + " uses a build-scoped compiler override");
                        Assert.That(Regex.Matches(File.ReadAllText(shader.Key),
                            @"^[\t ]*#pragma[\t ]+use_dxc[\t ]+vulkan[\t ]*\r?$", RegexOptions.Multiline).Count,
                            Is.EqualTo(shader.Value), shader.Key + " uses DXC for every Vulkan pass");
                    }
                    // Exercise the same finally path an aborted BuildPlayer call takes.
                    throw new ControlledBuildAbort();
                }
                catch (ControlledBuildAbort) { }
                finally { scope.Dispose(); }
                Assert.That(PlayerSettings.Android.androidVulkanDeviceFilterListAsset, Is.SameAs(source));
                foreach (var file in baseline) Assert.That(File.ReadAllBytes(file.Key), Is.EqualTo(file.Value), file.Key);
                foreach (var file in shaderBytes)
                {
                    Assert.That(File.ReadAllBytes(file.Key), Is.EqualTo(file.Value),
                        file.Key + " is restored byte for byte after an aborted build");
                    Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(file.Key);
                    Assert.That(shader, Is.Not.Null, file.Key);
                    Assert.That(ShaderUtil.ShaderHasError(shader), Is.False, file.Key + " reimports without shader errors");
                }
                Assert.That(File.ReadAllBytes(sourcePath), Is.EqualTo(sourceBytes), "the referenced custom filter asset is preserved");
                Assert.That(File.Exists(temporaryPath), Is.False);
                Assert.That(File.Exists(temporaryPath + ".meta"), Is.False);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(temporaryPath), Is.Null);
                Assert.DoesNotThrow(scope.Dispose, "Dispose is safe after the first restoration");
            }
            finally
            {
                try { scope?.Dispose(); }
                finally
                {
                    PlayerSettings.Android.androidVulkanDeviceFilterListAsset = original;
                    AssetDatabase.SaveAssets();
                    if (!AssetDatabase.DeleteAsset(sourcePath)) Destroy(source);
                    foreach (var file in originalBytes) File.WriteAllBytes(file.Key, file.Value);
                    foreach (var file in shaderBytes)
                    {
                        if (File.ReadAllBytes(file.Key).SequenceEqual(file.Value)) continue;
                        File.WriteAllBytes(file.Key, file.Value);
                        AssetDatabase.ImportAsset(file.Key, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                    }
                }
            }
        }

        private sealed class ControlledBuildAbort : Exception { }

        private static Dictionary<string, byte[]> SnapshotSettings()
        {
            return new[] { "ProjectSettings/ProjectSettings.asset", "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset",
                "Assets/Settings/UniversalRenderPipelineGlobalSettings.asset" }
                .Where(File.Exists).ToDictionary(path => path, File.ReadAllBytes);
        }

        private static VulkanDeviceFilterLists CreatePhone(VulkanDeviceFilterLists source, AndroidDeviceFilterData[] allow, AndroidDeviceFilterData[] deny)
        {
            MethodInfo method = typeof(AndroidFlatBuild).GetMethod("CreatePhoneVulkanFilters", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            return (VulkanDeviceFilterLists)method.Invoke(null, new object[] { source, allow, deny });
        }

        private static VulkanDeviceFilterLists NewCustomSource()
        {
            var source = new VulkanDeviceFilterLists("Custom filter test source");
            source.vulkanDeviceAllowFilters = new[] { Filter("ExistingAllow") };
            source.vulkanDeviceDenyFilters = new[] { Filter("ExistingDeny") };
            source.vulkanGraphicsJobsDeviceFilters = new[] { new VulkanGraphicsJobsDeviceFilterData { filter = Filter("ExistingJobs") } };
            return source;
        }

        private static VulkanDeviceFilterData Filter(string key) => new VulkanDeviceFilterData {
            vendorName = "^" + key + "Vendor$", deviceName = "^" + key + "Gpu$", brandName = "^" + key + "Brand$", productName = "^" + key + "Product$",
            androidOsVersionString = "", vulkanApiVersionString = "", driverVersionString = ""
        };

        private static AndroidDeviceFilterData LegacyFilter(string key) => new AndroidDeviceFilterData {
            vendorName = "^" + key + "Vendor$", deviceName = "^" + key + "Gpu$", brandName = "^" + key + "Brand$", productName = "^" + key + "Product$",
            androidOsVersionString = "", vulkanApiVersionString = "", driverVersionString = ""
        };

        private static bool Matches(VulkanDeviceFilterData filter, string brand, string product) =>
            Regex.IsMatch(brand, filter.brandName) && Regex.IsMatch(product, filter.productName);

        private static void AssertFilter(VulkanDeviceFilterData actual, AndroidDeviceFilterData expected)
        {
            Assert.That(actual.vendorName, Is.EqualTo(expected.vendorName));
            Assert.That(actual.deviceName, Is.EqualTo(expected.deviceName));
            Assert.That(actual.brandName, Is.EqualTo(expected.brandName));
            Assert.That(actual.productName, Is.EqualTo(expected.productName));
            Assert.That(actual.androidOsVersionString, Is.EqualTo(expected.androidOsVersionString));
            Assert.That(actual.vulkanApiVersionString, Is.EqualTo(expected.vulkanApiVersionString));
            Assert.That(actual.driverVersionString, Is.EqualTo(expected.driverVersionString));
        }

        private static void Destroy(Object value) { if (value != null) Object.DestroyImmediate(value); }
    }
}

#pragma warning restore CS0618
