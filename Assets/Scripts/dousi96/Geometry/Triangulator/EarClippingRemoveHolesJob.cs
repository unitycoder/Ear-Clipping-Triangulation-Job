﻿using Unity.Jobs;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;

using JacksonDunstan.NativeCollections;

namespace dousi96.Geometry.Triangulator
{
    [BurstCompile]
    public struct EarClippingRemoveHolesJob : IJob
    {
        [ReadOnly]
        public PolygonJobData Polygon;
        public NativeLinkedList<int> VertexIndexLinkedList;

        public void Execute()
        {
            //add contourn points and set the max ray length
            float minx = float.MaxValue;
            float maxx = float.MinValue;
            for (int i = 0; i < Polygon.NumContournPoints; ++i)
            {
                VertexIndexLinkedList.InsertAfter(VertexIndexLinkedList.Tail, i);
                if (Polygon.Vertices[i].x < minx)
                {
                    minx = Polygon.Vertices[i].x;
                }
                if (Polygon.Vertices[i].x > maxx)
                {
                    maxx = Polygon.Vertices[i].x;
                }
            }
            float maxRayLength = math.distance(minx, maxx);

            //create the array containing the holes data
            NativeArray<EarClippingHoleData> holesData = new NativeArray<EarClippingHoleData>(Polygon.NumHoles, Allocator.Temp);
            for (int i = 0; i < Polygon.NumHoles; ++i)
            {
                int indexMaxX = -1;
                float maxX = float.MinValue;
                for (int j = 0; j < Polygon.NumPointsPerHole[i]; ++j)
                {
                    float2 holeVertex = Polygon.GetHolePoint(i, j);
                    if (maxX < holeVertex.x)
                    {
                        maxX = holeVertex.x;
                        indexMaxX = j;
                    }
                }
                holesData[i] = new EarClippingHoleData(Polygon, i, indexMaxX);
            }
            holesData.Sort();

            //start the hole removing algorithm
            for (int i = 0; i < holesData.Length; ++i)
            {
                
                float2 M = Polygon.GetHolePoint(holesData[i].HoleIndex, holesData[i].IndexMaxX);

                float distanceMI = float.MaxValue;
                float2 I = new float2();
                NativeLinkedList<int>.Enumerator vi = VertexIndexLinkedList.Head;
                
                for (NativeLinkedList<int>.Enumerator contournEnum = VertexIndexLinkedList.Head;
                    contournEnum.IsValid;
                    contournEnum.MoveNext())
                {
                    NativeLinkedList<int>.Enumerator contournNextEnum = (!contournEnum.Next.IsValid) ? VertexIndexLinkedList.Head : contournEnum.Next;

                    //intersect the ray
                    float2 intersection;
                    bool areSegmentsIntersecting = Geometry2DUtils.SegmentsIntersection(M, new float2(maxRayLength, M.y), Polygon.Vertices[contournEnum.Value], Polygon.Vertices[contournNextEnum.Value], out intersection);
                    if (!areSegmentsIntersecting)
                    {
                        continue;
                    }

                    float distance = math.distance(M, intersection);
                    if (distance < distanceMI)
                    {
                        vi = contournEnum;
                        I = intersection;
                        distanceMI = distance;
                    }
                }

                NativeLinkedList<int>.Enumerator selectedBridgePoint;
                if (math.distance(I, Polygon.Vertices[vi.Value]) <= float.Epsilon)
                {
                    //I is a vertex of the outer polygon
                    selectedBridgePoint = vi;
                }
                else
                {
                    NativeLinkedList<int>.Enumerator viplus1 = (!vi.Next.IsValid) ? VertexIndexLinkedList.Head : vi.Next;
                    //I is an interior point of the edge <V(i), V(i+1)>, select P as the maximum x-value endpoint of the edge
                    NativeLinkedList<int>.Enumerator P = (Polygon.Vertices[viplus1.Value].x  > Polygon.Vertices[vi.Value].x) ? viplus1 : vi;
                    selectedBridgePoint = P;
                    //Search the reflex vertices of the outer polygon (not including P if it happens to be reflex)                    
                    float minAngle = float.MaxValue;
                    float minDist = float.MaxValue;                    
                    for (NativeLinkedList<int>.Enumerator contournEnum = VertexIndexLinkedList.Head;
                        contournEnum.IsValid;
                        contournEnum.MoveNext())
                    {
                        //not including P
                        if (contournEnum == P)
                        {
                            continue;
                        }

                        int iReflexCur = contournEnum.Value;
                        int iReflexPrev = (!contournEnum.Prev.IsValid) ? VertexIndexLinkedList.Tail.Value : contournEnum.Prev.Value;
                        int iReflexNext = (!contournEnum.Next.IsValid) ? VertexIndexLinkedList.Head.Value : contournEnum.Next.Value;

                        bool isReflex = Geometry2DUtils.IsVertexReflex(Polygon.Vertices[iReflexPrev], Polygon.Vertices[iReflexCur], Polygon.Vertices[iReflexNext], true);
                        if (!isReflex)
                        {
                            continue;
                        }

                        bool isReflexVertexInsideMIPTriangle = Geometry2DUtils.IsInsideTriangle(Polygon.Vertices[iReflexCur], M, I, Polygon.Vertices[P.Value]);
                        if (isReflexVertexInsideMIPTriangle)
                        {
                            //search for the reflex vertex R that minimizes the angle between (1,0) and the line segment M-R
                            float2 atan2 = Polygon.Vertices[iReflexCur] - M;
                            float angleRMI = math.atan2(atan2.y, atan2.x);
                            if (angleRMI < minAngle)
                            {
                                selectedBridgePoint = contournEnum;
                                minAngle = angleRMI;
                            }
                            else if (math.abs(angleRMI - minAngle) <= float.Epsilon)
                            {
                                //same angle
                                float distanceRM = math.lengthsq(atan2);
                                if (distanceRM < minDist)
                                {
                                    selectedBridgePoint = contournEnum;
                                    minDist = distanceRM;
                                }
                            } 
                        }
                    }
                }

                //insert the bridge points and the holes points inside the linked list
                int holeStartIndex = Polygon.StartPointsHoles[holesData[i].HoleIndex];
                int holeLength = Polygon.NumPointsPerHole[holesData[i].HoleIndex];
                int holeEndIndex = holeStartIndex + holeLength;
                int internalMaxXIndex = holeStartIndex + holesData[i].IndexMaxX;
                VertexIndexLinkedList.InsertAfter(selectedBridgePoint, selectedBridgePoint.Value);
                for (int j = internalMaxXIndex, count = 0;
                    count < holeLength;
                    ++count, j = (j == holeStartIndex) ? holeEndIndex - 1 : j - 1)
                {
                    VertexIndexLinkedList.InsertAfter(selectedBridgePoint, j);
                }
                VertexIndexLinkedList.InsertAfter(selectedBridgePoint, internalMaxXIndex);
            }

            holesData.Dispose();
        }
    }
}
