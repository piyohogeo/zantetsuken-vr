using System.Reflection;
using NUnit.Framework;
using Unity.Jobs;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class WorkItemProfilerFlowTests
    {
        private struct FlowEventJob : IJob
        {
            public WorkItemProfilerFlow Flow;

            public void Execute()
            {
                Flow.Next();
                Flow.End();
            }
        }

        [Test]
        public void DefaultFlow_IsInvalidAndNoOp()
        {
            WorkItemProfilerFlow flow = default;

            Assert.That(flow.IsValid, Is.False);

            // Must not throw for a default handle.
            flow.Begin();
            flow.Next();
            flow.ParallelNext();
            flow.End();
        }

        [Test]
        public void Create_KeepsTaskId()
        {
            WorkItemProfilerFlow flow = WorkItemProfilerFlow.Create(12345);
            Assert.That(flow.TaskId, Is.EqualTo(12345));
        }

        [Test]
        public void ValidHandle_CanEmitAllEvents()
        {
            WorkItemProfilerFlow flow = WorkItemProfilerFlow.Create(7);

            // Does not require FlowId != 0 when the Profiler is disabled.
            flow.Begin();
            flow.Next();
            flow.ParallelNext();
            flow.End();
        }

        [Test]
        public void Handle_HasNoReferenceTypeFields()
        {
            FieldInfo[] fields = typeof(WorkItemProfilerFlow).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " is not a value type");
            }
        }

        [Test]
        public void Flow_CanBePassedToJobByValue()
        {
            WorkItemProfilerFlow flow = WorkItemProfilerFlow.Create(99);

            FlowEventJob job = new FlowEventJob { Flow = flow };
            job.Schedule().Complete();

            Assert.That(flow.TaskId, Is.EqualTo(99));
        }
    }
}
