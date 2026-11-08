#include "physics_collision.slang"

// Box face axes give a repeatable penetration normal for touching faces, edges and corners.
// Separated boxes and edge-axis minima use the common convex distance implementation.
struct BoxBoxCollision : IDeviceCollisionNarrowPhase
{
    DeviceGeometryDistance distance(uint work,DeviceGeometryInput first,DeviceGeometryInput second)
    {
        float3 a[3]={rotateDeviceDirection(first.rotation,float3(1,0,0)),
            rotateDeviceDirection(first.rotation,float3(0,1,0)),rotateDeviceDirection(first.rotation,float3(0,0,1))};
        float3 b[3]={rotateDeviceDirection(second.rotation,float3(1,0,0)),
            rotateDeviceDirection(second.rotation,float3(0,1,0)),rotateDeviceDirection(second.rotation,float3(0,0,1))};
        float3 delta=second.position-first.position;
        float best=-3.402823466e38f;
        float3 normal=float3(1,0,0);
        uint selected=0;
        for(uint index=0;index<15;index++)
        {
            float3 axis;
            if(index<3) axis=a[index];
            else if(index<6) axis=b[index-3];
            else
            {
                uint edge=index-6;
                axis=cross(a[edge/3],b[edge%3]);
                float squared=dot(axis,axis);
                if(squared<=1e-12f) continue;
                axis*=rsqrt(squared);
            }
            float radius=0;
            for(uint dimension=0;dimension<3;dimension++)
                radius+=abs(dot(axis,a[dimension]))*first.halfExtents[dimension]
                    +abs(dot(axis,b[dimension]))*second.halfExtents[dimension];
            float projection=dot(delta,axis);
            float separation=abs(projection)-radius;
            if(separation>best+DetectionParameters.tolerance)
            {
                best=separation; normal=projection<0?-axis:axis; selected=index;
            }
        }
        DeviceConvexCollision<DeviceBox,DeviceBox> convex;
        if(best>0||selected>=6) return convex.distance(work,first,second);
        DeviceBox box;
        DeviceFeaturePoint firstPoint=box.support(first,normal),secondPoint=box.support(second,-normal);
        DeviceGeometryDistance result;
        result.firstPoint=firstPoint.point; result.secondPoint=secondPoint.point;
        result.firstFeature=firstPoint.feature; result.secondFeature=secondPoint.feature;
        result.normal=normal; result.separation=best;
        return result;
    }
    uint manifold(uint work,DeviceGeometryInput first,DeviceGeometryInput second,
        DeviceGeometryDistance distance,out DeviceGeometryDistance points[4])
    { return manifoldDevicePair<DeviceBox,DeviceBox>(work,first,second,distance,points); }
};
