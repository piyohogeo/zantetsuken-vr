using System;
using System.IO;
using System.Text;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the two filesystem boundaries the Run
    /// initialization coordinator needs: the provisioner that creates one
    /// brand-new, empty Run root, and the writer that commits one marker
    /// atomically. They run against the real filesystem, in a temporary
    /// directory outside the repository, and are torn down whatever happens.
    /// </summary>
    /// <remarks>
    /// The last fixture here runs the real coordinator with both
    /// implementations injected, so the four writes a Run initialization
    /// actually performs - staging root, staging run.init, final root, final
    /// run.init, then both run.ready markers - are exercised end to end on
    /// disk and produce a usable session issue.
    /// </remarks>
    public class CaptureRunInitializationFilesystemContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private string _root;

        [SetUp]
        public void SetUp()
        {
            // Outside the repository, and unique per test so one failure
            // cannot leak into the next.
            _root = Path.Combine(
                Path.GetTempPath(),
                "zantetsu-run-init-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root == null || !Directory.Exists(_root))
            {
                return;
            }

            // A reparse point has to go as an entry of its own; a recursive
            // delete would try to walk into it.
            foreach (string entry in Directory.GetDirectories(_root))
            {
                DirectoryInfo info = new DirectoryInfo(entry);
                if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    continue;
                }

                try
                {
                    info.Delete(false);
                }
                catch (Exception)
                {
                }
            }

            try
            {
                Directory.Delete(_root, true);
            }
            catch (Exception)
            {
                // A leftover handle is not this test's verdict; the temporary
                // directory is the OS's to reclaim.
            }
        }

        // -------------------------------------------------------------------
        // Root provisioner
        // -------------------------------------------------------------------

        [Test]
        public void ProvisionNew_CreatesAnEmptyRunRoot_AndIssuesItsReceipt()
        {
            CaptureRunRootOsProvisioner provisioner = CaptureRunRootOsProvisioner.Create();
            CaptureRunRootProvisionOperation operation = StagingProvision(MakeLayout());

            Assert.That(Directory.Exists(operation.RunRoot), Is.False);

            CaptureRunRootProvisionReceipt receipt = provisioner.ProvisionNew(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IssuedBy, Is.SameAs(provisioner));
            Assert.That(receipt.Operation, Is.SameAs(operation));

            Assert.That(Directory.Exists(operation.RunRoot), Is.True);

            // Empty means empty: no chunks directory, no marker, nothing.
            Assert.That(Directory.GetFileSystemEntries(operation.RunRoot), Is.Empty);
        }

        [Test]
        public void ProvisionNew_RejectsAnExistingRunRoot_AndLeavesItAlone()
        {
            CaptureRunRootOsProvisioner provisioner = CaptureRunRootOsProvisioner.Create();
            CaptureRunRootProvisionOperation operation = StagingProvision(MakeLayout());

            Directory.CreateDirectory(operation.RunRoot);
            string witness = Path.Combine(operation.RunRoot, "witness.txt");
            File.WriteAllText(witness, "kept");

            Assert.That(() => provisioner.ProvisionNew(operation), Throws.Exception,
                "an existing Run root is never provisioned");

            Assert.That(File.Exists(witness), Is.True, "the existing root must be untouched");
            Assert.That(File.ReadAllText(witness), Is.EqualTo("kept"));
        }

        [Test]
        public void ProvisionNew_RejectsATargetReachedThroughAReparsePoint()
        {
            string real = Path.Combine(_root, "real");
            string link = Path.Combine(_root, "link");
            Directory.CreateDirectory(real);

            if (!TryCreateDirectoryJunction(link, real))
            {
                Assert.Ignore("This environment does not allow creating a directory junction.");
            }

            // The base root is trusted by contract; the junction sits on the
            // path the Run root would be reached through.
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                link, Path.Combine(_root, "final"), 1);
            CreateTrustedBase(layout.StagingRunRoot);
            CaptureRunRootProvisionOperation operation = StagingProvision(layout);

            Assert.That(
                () => CaptureRunRootOsProvisioner.Create().ProvisionNew(operation), Throws.Exception,
                "a Run root reached through a reparse point is never provisioned");

            // The junction is never traversed, so nothing is created behind
            // it - not even an empty directory to be cleaned up later.
            Assert.That(
                Directory.Exists(Path.Combine(real, "runs", "run-1")), Is.False,
                "a Run root must never be created through a junction");
        }

        [Test]
        public void ProvisionNew_RejectsNull_BeforeAnySideEffect()
        {
            CaptureRunRootOsProvisioner provisioner = CaptureRunRootOsProvisioner.Create();

            Assert.Throws<ArgumentNullException>(() => provisioner.ProvisionNew(null));
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        // -------------------------------------------------------------------
        // Marker atomic writer
        // -------------------------------------------------------------------

        [Test]
        public void WriteAtomic_WritesTheCanonicalBytes_AndLeavesNoTemporary()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootOsProvisioner provisioner = CaptureRunRootOsProvisioner.Create();
            CaptureRunMarkerOsAtomicWriter writer = CaptureRunMarkerOsAtomicWriter.Create();

            provisioner.ProvisionNew(StagingProvision(layout));

            CaptureRunMarkerWriteOperation operation = MakeBatch(layout).StagingInitialization;

            CaptureRunMarkerWriteReceipt receipt = writer.WriteAtomic(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IssuedBy, Is.SameAs(writer));
            Assert.That(receipt.Operation, Is.SameAs(operation));

            Assert.That(File.Exists(operation.FinalPath), Is.True);
            Assert.That(File.Exists(operation.TemporaryPath), Is.False);
            Assert.That(
                File.ReadAllBytes(operation.FinalPath), Is.EqualTo(operation.GetCanonicalBytes()),
                "the final marker must be exactly the canonical bytes");
        }

        [Test]
        public void WriteAtomic_NeverOverwritesAnExistingFinalMarker()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootOsProvisioner.Create().ProvisionNew(StagingProvision(layout));
            CaptureRunMarkerOsAtomicWriter writer = CaptureRunMarkerOsAtomicWriter.Create();

            CaptureRunMarkerWriteOperation operation = MakeBatch(layout).StagingInitialization;

            File.WriteAllText(operation.FinalPath, "existing", Encoding.ASCII);

            Assert.That(() => writer.WriteAtomic(operation), Throws.Exception,
                "an existing final marker is a failure, never an overwrite");

            Assert.That(File.ReadAllText(operation.FinalPath), Is.EqualTo("existing"));
        }

        [Test]
        public void WriteAtomic_RefusesADirectoryThatDoesNotResolveToTheOperationsPath()
        {
            string real = Path.Combine(_root, "real");
            string link = Path.Combine(_root, "link");
            Directory.CreateDirectory(real);

            if (!TryCreateDirectoryJunction(link, real))
            {
                Assert.Ignore("This environment does not allow creating a directory junction.");
            }

            // The operation names its marker under the junction, so the
            // directory the writer opens resolves somewhere else than the
            // operation asked for. The Run root itself is created behind the
            // junction by this test, not by the provisioner, so the writer is
            // the only thing under examination.
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                link, Path.Combine(_root, "final"), 1);
            CaptureRunMarkerWriteOperation operation = MakeBatch(layout).StagingInitialization;
            Directory.CreateDirectory(Path.Combine(real, "runs", "run-1"));

            Assert.That(
                () => CaptureRunMarkerOsAtomicWriter.Create().WriteAtomic(operation), Throws.Exception,
                "a directory that resolves elsewhere is never written to");

            // And nothing was written through the link either.
            Assert.That(
                Directory.GetFileSystemEntries(Path.Combine(real, "runs", "run-1")), Is.Empty,
                "no marker and no temporary entry may appear in the real directory");
        }

        [Test]
        public void WriteAtomic_RejectsNull_BeforeAnySideEffect()
        {
            CaptureRunMarkerOsAtomicWriter writer = CaptureRunMarkerOsAtomicWriter.Create();

            Assert.Throws<ArgumentNullException>(() => writer.WriteAtomic(null));
            Assert.That(Directory.GetFileSystemEntries(_root), Is.Empty);
        }

        [Test]
        public void WriteAtomic_HandlesAllFourMarkerOperationsOfOneRun()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootOsProvisioner provisioner = CaptureRunRootOsProvisioner.Create();
            CaptureRunMarkerOsAtomicWriter writer = CaptureRunMarkerOsAtomicWriter.Create();

            provisioner.ProvisionNew(StagingProvision(layout));
            provisioner.ProvisionNew(FinalProvision(layout));

            CaptureRunInitializationWriteBatch batch = MakeBatch(layout);

            CaptureRunMarkerWriteOperation[] operations =
            {
                batch.StagingInitialization,
                batch.FinalInitialization,
                batch.StagingReady,
                batch.FinalReady,
            };

            foreach (CaptureRunMarkerWriteOperation operation in operations)
            {
                CaptureRunMarkerWriteReceipt receipt = writer.WriteAtomic(operation);
                Assert.That(receipt.IssuedBy, Is.SameAs(writer));
                Assert.That(receipt.Operation, Is.SameAs(operation));
                Assert.That(
                    File.ReadAllBytes(operation.FinalPath),
                    Is.EqualTo(operation.GetCanonicalBytes()));
                Assert.That(File.Exists(operation.TemporaryPath), Is.False);
            }
        }

        // -------------------------------------------------------------------
        // The real coordinator, on the real filesystem
        // -------------------------------------------------------------------

        [Test]
        public void TheRealCoordinator_InitializesOneRunOnDisk_AndYieldsAUsableIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationWriteBatch batch = MakeBatch(layout);

            CaptureRunInitializationExecutionReceipt receipt =
                new CaptureRunInitializationExecutionCoordinator(
                    CaptureRunRootOsProvisioner.Create(),
                    CaptureRunMarkerOsAtomicWriter.Create())
                .Execute(batch);

            Assert.That(receipt, Is.Not.Null);

            // Both roots exist and hold exactly their two markers - the
            // chunks directory is the chunk file session's to create, later.
            foreach (string runRoot in new[] { layout.StagingRunRoot, layout.FinalRunRoot })
            {
                Assert.That(Directory.Exists(runRoot), Is.True);
                Assert.That(
                    Directory.GetDirectories(runRoot), Is.Empty,
                    "no chunks directory exists yet");
                Assert.That(
                    Directory.GetFiles(runRoot).Length, Is.EqualTo(2),
                    "an initialized Run root holds its initialization and ready markers");
            }

            Assert.That(File.Exists(batch.StagingInitialization.FinalPath), Is.True);
            Assert.That(File.Exists(batch.FinalInitialization.FinalPath), Is.True);
            Assert.That(File.Exists(batch.StagingReady.FinalPath), Is.True);
            Assert.That(File.Exists(batch.FinalReady.FinalPath), Is.True);

            // And the evidence a Run actually needs downstream.
            CaptureRunInitializationReadyEvidence evidence =
                CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            Assert.That(evidence, Is.Not.Null);
            Assert.That(evidence.IsValid, Is.True);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static CaptureRunInitializationWriteBatch MakeBatch(CaptureRunRootLayout layout)
        {
            return new CaptureRunInitializationWriteBatch(
                new CaptureRunInitializationDocumentSet(layout, InitId));
        }

        /// <summary>
        /// A layout whose trusted base roots already exist, including the one
        /// relative parent the layout puts a Run under. The provisioner
        /// creates the Run root itself and nothing above it.
        /// </summary>
        private CaptureRunRootLayout MakeLayout()
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(_root, "staging"), Path.Combine(_root, "final"), 1);

            CreateTrustedBase(layout.StagingRunRoot);
            CreateTrustedBase(layout.FinalRunRoot);
            return layout;
        }

        private static void CreateTrustedBase(string runRoot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(runRoot));
        }

        private static CaptureRunRootProvisionOperation StagingProvision(CaptureRunRootLayout layout)
        {
            return new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Staging);
        }

        private static CaptureRunRootProvisionOperation FinalProvision(CaptureRunRootLayout layout)
        {
            return new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Final);
        }

        /// <summary>
        /// Creates a directory junction with the OS's own tool, and says so if
        /// the environment will not allow it. No new seam is introduced for
        /// this: it is the real reparse point the provisioner must refuse.
        /// </summary>
        private static bool TryCreateDirectoryJunction(string link, string target)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo start =
                    new System.Diagnostics.ProcessStartInfo("cmd.exe")
                    {
                        Arguments = "/c mklink /J \"" + link + "\" \"" + target + "\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                    };

                using (System.Diagnostics.Process process = System.Diagnostics.Process.Start(start))
                {
                    process.WaitForExit(10000);
                    return process.HasExited && process.ExitCode == 0 && Directory.Exists(link);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
