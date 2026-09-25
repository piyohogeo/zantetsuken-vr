using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    public class D5JointDefaultsTests
    {
        [Serializable] class State
        {
            public Vector3 axis, secondary, anchor, connectedAnchor;
            public bool autoAnchor, collision, forceInfinite, torqueInfinite;
            public ConfigurableJointMotion x,y,z,ax,ay,az;
            public JointProjectionMode projection;
            public float limit,bounciness,contactDistance;
            public float[] drives;
        }
        static State Read(ConfigurableJoint j)=>new State {
            axis=j.axis,secondary=j.secondaryAxis,anchor=j.anchor,connectedAnchor=j.connectedAnchor,
            autoAnchor=j.autoConfigureConnectedAnchor,collision=j.enableCollision,
            forceInfinite=float.IsPositiveInfinity(j.breakForce),torqueInfinite=float.IsPositiveInfinity(j.breakTorque),
            x=j.xMotion,y=j.yMotion,z=j.zMotion,ax=j.angularXMotion,ay=j.angularYMotion,az=j.angularZMotion,
            projection=j.projectionMode,limit=j.linearLimit.limit,bounciness=j.linearLimit.bounciness,contactDistance=j.linearLimit.contactDistance,
            drives=Drives(j)};
        static float[] Drives(ConfigurableJoint j)
        {
            var drives=new[]{j.xDrive,j.yDrive,j.zDrive,j.angularXDrive,j.angularYZDrive,j.slerpDrive};var values=new float[18];
            for(int i=0;i<drives.Length;i++){values[i*3]=drives[i].positionSpring;values[i*3+1]=drives[i].positionDamper;values[i*3+2]=drives[i].maximumForce;}
            return values;
        }
        static void Defaults(ConfigurableJoint j)
        {
            Assert.That(j.anchor,Is.EqualTo(Vector3.zero));Assert.That(j.projectionMode,Is.EqualTo(JointProjectionMode.None));
            Assert.That(j.breakForce,Is.EqualTo(float.PositiveInfinity));Assert.That(j.breakTorque,Is.EqualTo(float.PositiveInfinity));
            Assert.That(j.enableCollision,Is.False);
        }
        // Frozen pre-D5 configuration, including all 17 setters. Independent of the product Configure method.
        static ConfigurableJoint Baseline(GameObject p,Rigidbody n,float3 normal)
        {
            float3 axis=math.normalize(normal), absolute=math.abs(axis);
            float3 candidate=absolute.x<=absolute.y&&absolute.x<=absolute.z?new float3(1,0,0)
                :absolute.y<=absolute.z?new float3(0,1,0):new float3(0,0,1);
            float3 orthogonal=candidate-axis*math.dot(axis,candidate);
            float length=math.length(orthogonal);float3 secondary=length>0?orthogonal/length:new float3(0,1,0);
            var j=p.AddComponent<ConfigurableJoint>();j.connectedBody=n;j.autoConfigureConnectedAnchor=false;
            j.axis=axis;j.secondaryAxis=secondary;j.anchor=Vector3.zero;j.connectedAnchor=axis;
            j.xMotion=ConfigurableJointMotion.Limited;j.yMotion=j.zMotion=ConfigurableJointMotion.Locked;
            j.angularXMotion=j.angularYMotion=j.angularZMotion=ConfigurableJointMotion.Locked;
            j.linearLimit=new SoftJointLimit{limit=1,bounciness=0,contactDistance=0};
            j.projectionMode=JointProjectionMode.None;j.breakForce=float.PositiveInfinity;j.breakTorque=float.PositiveInfinity;j.enableCollision=false;
            return j;
        }
        [TestCase(0,false)][TestCase(1,false)][TestCase(2,false)][TestCase(3,false)]
        [TestCase(4,false)][TestCase(5,false)][TestCase(6,false)][TestCase(7,false)]
        [TestCase(0,true)][TestCase(1,true)][TestCase(2,true)][TestCase(3,true)]
        [TestCase(4,true)][TestCase(5,true)][TestCase(6,true)][TestCase(7,true)]
        public void D5_FreshJoint_MatchesAllSetterBaseline_AxisAndActivation(int index,bool activate)
        {
            var axes=new[]{new float3(1,0,0),new float3(0,1,0),new float3(0,0,1),new float3(-1,0,0),
                new float3(0,-1,0),new float3(0,0,-1),new float3(1,2,3),new float3(-3,2,-1)};
            var objects=new GameObject[4];
            try
            {
                for(int i=0;i<4;i++) {objects[i]=new GameObject("D5 synthetic joint");objects[i].SetActive(false);objects[i].AddComponent<Rigidbody>();}
                var fresh=objects[0].AddComponent<ConfigurableJoint>();Defaults(fresh);UnityEngine.Object.DestroyImmediate(fresh);
                var positive=new PhysicsOwnerSide(true,objects[0],objects[0],objects[0].GetComponent<Rigidbody>());
                var negative=new PhysicsOwnerSide(false,objects[1],objects[1],objects[1].GetComponent<Rigidbody>());
                var actual=ProvisionalSeparation.Configure(positive,negative,axes[index]);
                var expected=Baseline(objects[2],objects[3].GetComponent<Rigidbody>(),axes[index]);
                if(activate)foreach(var go in objects)go.SetActive(true);
                Defaults(actual);Assert.That(actual.connectedBody,Is.SameAs(negative.Body));
                Assert.That(actual.GetComponent<Rigidbody>(),Is.SameAs(positive.Body));
                Assert.That(JsonUtility.ToJson(Read(actual)),Is.EqualTo(JsonUtility.ToJson(Read(expected))));
                Assert.That(Vector3.Distance(actual.axis,math.normalize(axes[index])),Is.LessThan(2e-5f));
                Assert.That(actual.connectedAnchor,Is.EqualTo(actual.axis));
                Assert.That(actual.xMotion,Is.EqualTo(ConfigurableJointMotion.Limited));
                Assert.That(actual.linearLimit.limit,Is.EqualTo(1));Assert.That(actual.autoConfigureConnectedAnchor,Is.False);
            }
            finally {foreach(var go in objects)if(go!=null)UnityEngine.Object.DestroyImmediate(go);}
        }
    }
}
