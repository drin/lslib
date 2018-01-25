﻿using System;
using System.Collections.Generic;
using System.Linq;
using LSLib.Granny.GR2;
using LSLib.Granny.Model;
using LSLib.Granny.Model.CurveData;
using OpenTK;

namespace LSLib.Granny
{
    public class ColladaAnimation
    {
        private animation Animation;
        private Dictionary<String, ColladaSource> Sources;
        private List<Matrix4> Transforms;
        private List<Single> Times;
        private Bone Bone;

        private void ImportSources()
        {
            Sources = new Dictionary<String, ColladaSource>();
            foreach (var item in Animation.Items)
            {
                if (item is source)
                {
                    var src = ColladaSource.FromCollada(item as source);
                    Sources.Add(src.id, src);
                }
            }
        }

        private void ImportSampler()
        {
            sampler sampler = null;
            foreach (var item in Animation.Items)
            {
                if (item is sampler)
                {
                    sampler = item as sampler;
                    break;
                }
            }

            if (sampler == null)
                throw new ParsingException("Animation " + Animation.id + " has no sampler!");

            ColladaSource inputSource = null, outputSource = null, interpolationSource = null;
            foreach (var input in sampler.input)
            {
                if (input.source[0] != '#')
                    throw new ParsingException("Only ID references are supported for animation input sources");

                ColladaSource source;
                if (!Sources.TryGetValue(input.source.Substring(1), out source))
                    throw new ParsingException("Animation sampler " + input.semantic + " references nonexistent source: " + input.source);

                switch (input.semantic)
                {
                    case "INPUT":
                        inputSource = source;
                        break;

                    case "OUTPUT":
                        outputSource = source;
                        break;

                    case "INTERPOLATION":
                        interpolationSource = source;
                        break;

                    default:
                        break;
                }
            }

            if (inputSource == null || outputSource == null || interpolationSource == null)
                throw new ParsingException("Animation " + Animation.id + " must have an INPUT, OUTPUT and INTERPOLATION sampler input!");

            if (!inputSource.FloatParams.TryGetValue("TIME", out Times))
                Times = inputSource.FloatParams.Values.SingleOrDefault();

            if (Times == null)
                throw new ParsingException("Animation " + Animation.id + " INPUT must have a TIME parameter!");

            if (!outputSource.MatrixParams.TryGetValue("TRANSFORM", out Transforms))
                Transforms = outputSource.MatrixParams.Values.SingleOrDefault();

            if (Transforms == null)
                throw new ParsingException("Animation " + Animation.id + " OUTPUT must have a TRANSFORM parameter!");

            if (Transforms.Count != Times.Count)
                throw new ParsingException("Animation " + Animation.id + " has different time and transform counts!");

            for (var i = 0; i < Transforms.Count; i++ )
            {
                var m = Transforms[i];
                m.Transpose();
                Transforms[i] = m;
            }
        }

        private void ImportChannel(Skeleton skeleton)
        {
            channel channel = null;
            foreach (var item in Animation.Items)
            {
                if (item is channel)
                {
                    channel = item as channel;
                    break;
                }
            }

            if (channel == null)
                throw new ParsingException("Animation " + Animation.id + " has no channel!");

            var parts = channel.target.Split(new char[] { '/' });
            if (parts.Length != 2)
                throw new ParsingException("Unsupported channel target format: " + channel.target);

            Bone bone = null;
            if (!skeleton.BonesByID.TryGetValue(parts[0], out bone))
                throw new ParsingException("Animation channel references nonexistent bone: " + parts[0]);

            if (bone.TransformSID != parts[1])
                throw new ParsingException("Animation channel references nonexistent transform or transform is not float4x4: " + channel.target);

            Bone = bone;
        }

        public bool ImportFromCollada(animation colladaAnim, Skeleton skeleton)
        {
            Animation = colladaAnim;
            ImportSources();
            ImportSampler();

            // Avoid importing empty animations
            if (Transforms.Count == 0)
                return false;

            ImportChannel(skeleton);
            return true;
        }

        private Matrix3 ScaleToScaleShear(Vector3 scale)
        {
            var m = Matrix3.Identity;
            m[0, 0] *= scale.X;
            m[1, 1] *= scale.Y;
            m[2, 2] *= scale.Z;
            return m;
        }

        private void RemoveTrivialFrames(ref List<Single> times, ref List<Vector3> transforms)
        {
            var i = 1;
            while (i < transforms.Count - 1)
            {
                Vector3 v0 = transforms[i - 1],
                    v1 = transforms[i],
                    v2 = transforms[i + 1];

                Single t0 = times[i - 1],
                    t1 = times[i],
                    t2 = times[i + 1];

                Single alpha = (t1 - t0) / (t2 - t0);
                Vector3 v1l = Vector3.Lerp(v0, v2, alpha);

                if ((v1 - v1l).Length < 0.001f)
                {
                    times.RemoveAt(i);
                    transforms.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            if (transforms.Count == 2 && (transforms[0] - transforms[1]).Length < 0.0001f)
            {
                times.RemoveAt(1);
                transforms.RemoveAt(1);
            }
        }

        private void RemoveTrivialFrames(ref List<Single> times, ref List<Quaternion> transforms)
        {
            var i = 1;
            while (i < transforms.Count - 1)
            {
                Quaternion v0 = transforms[i - 1],
                    v1 = transforms[i],
                    v2 = transforms[i + 1];

                Single t0 = times[i - 1],
                    t1 = times[i],
                    t2 = times[i + 1];

                Single alpha = (t1 - t0) / (t2 - t0);
                Quaternion v1l = Quaternion.Slerp(v0, v2, alpha);

                if ((v1 - v1l).Length < 0.001f)
                {
                    times.RemoveAt(i);
                    transforms.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            if (transforms.Count == 2 && (transforms[0] - transforms[1]).Length < 0.0001f)
            {
                times.RemoveAt(1);
                transforms.RemoveAt(1);
            }
        }

        private void RemoveTrivialFrames(ref List<Single> times, ref List<Matrix3> transforms)
        {
            var i = 2;
            while (i < transforms.Count)
            {
                Matrix3 t0 = transforms[i - 2],
                    t1 = transforms[i - 1],
                    t2 = transforms[i];

                float diff1 = 0.0f, diff2 = 0.0f;
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        diff1 += Math.Abs(t1[x, y] - t0[x, y]);
                        diff2 += Math.Abs(t2[x, y] - t1[x, y]);
                    }
                }

                if (diff1 < 0.001f && diff2 < 0.001f)
                {
                    times.RemoveAt(i);
                    transforms.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }

            if (transforms.Count == 2)
            {
                float diff = 0.0f;
                for (var x = 0; x < 3; x++)
                {
                    for (var y = 0; y < 3; y++)
                    {
                        diff += Math.Abs(transforms[0][x, y] - transforms[1][x, y]);
                    }
                }

                if (diff < 0.001f)
                {
                    times.RemoveAt(1);
                    transforms.RemoveAt(1);
                }
            }
        }

        public TransformTrack MakeTrack()
        {
            var track = new TransformTrack();
            track.Flags = 0;
            track.Name = Bone.Name;

            var positions = Transforms.Select(m => m.ExtractTranslation()).ToList();
            var rotations = Transforms.Select(m => m.ExtractRotation()).ToList();
            var scales = Transforms.Select(m => ScaleToScaleShear(m.ExtractScale())).ToList();
            for (var i = 0; i < rotations.Count; i++)
            {
                var r = rotations[i];
                if (r.W < 0)
                {
                    rotations[i] = new Quaternion(-r.X, -r.Y, -r.Z, -r.W);
                }
            }

            // Quaternion sign fixup
            // Since GR2 interpolation operates on the raw XYZ values of the quaternion, two subsequent quaternions
            // that express the same rotation (eg. [1, 0.5, 0.5, -0.5] and [1, -0.5, -0.5, 0.5]) will result in a 360 deg
            // rotation during the animation. Shuffle XYZ signs around to make this less likely to happen
            for (var i = 1; i < rotations.Count; i++)
            {
                var r0 = rotations[i - 1];
                var r1 = rotations[i];
                var distance = Math.Abs(r0.X - r1.X) + Math.Abs(r0.Y - r1.Y) + Math.Abs(r0.Z - r1.Z);
                var inverseDistance = Math.Abs(r0.X + r1.X) + Math.Abs(r0.Y + r1.Y) + Math.Abs(r0.Z + r1.Z);
                if (distance > inverseDistance)
                {
                    rotations[i] = new Quaternion(-r1.X, -r1.Y, -r1.Z, r1.W);
                }
            }

            var posTimes = Times.ToList();
            var minPositions = positions;
            RemoveTrivialFrames(ref posTimes, ref minPositions);
            if (minPositions.Count == 1)
            {
                var posCurve = new D3Constant32f();
                posCurve.CurveDataHeader_D3Constant32f = new CurveDataHeader { Format = (int)CurveFormat.D3Constant32f, Degree = 2 };
                posCurve.Controls = new float[3] { minPositions[0].X, minPositions[0].Y, minPositions[0].Z };
                track.PositionCurve = new AnimationCurve { CurveData = posCurve };
            }
            else
            {
                var posCurve = new DaK32fC32f();
                posCurve.CurveDataHeader_DaK32fC32f = new CurveDataHeader { Format = (int)CurveFormat.DaK32fC32f, Degree = 2 };
                posCurve.SetKnots(posTimes);
                posCurve.SetPoints(minPositions);
                track.PositionCurve = new AnimationCurve { CurveData = posCurve };
            }

            var rotTimes = Times.ToList();
            var minRotations = rotations;
            RemoveTrivialFrames(ref rotTimes, ref minRotations);
            if (minRotations.Count == 1)
            {
                var rotCurve = new D4Constant32f();
                rotCurve.CurveDataHeader_D4Constant32f = new CurveDataHeader { Format = (int)CurveFormat.D4Constant32f, Degree = 2 };
                rotCurve.Controls = new float[4] { minRotations[0].X, minRotations[0].Y, minRotations[0].Z, minRotations[0].W };
                track.OrientationCurve = new AnimationCurve { CurveData = rotCurve };
            }
            else
            {
                var rotCurve = new DaK32fC32f();
                rotCurve.CurveDataHeader_DaK32fC32f = new CurveDataHeader { Format = (int)CurveFormat.DaK32fC32f, Degree = 2 };
                rotCurve.SetKnots(rotTimes);
                rotCurve.SetQuaternions(minRotations);
                track.OrientationCurve = new AnimationCurve { CurveData = rotCurve };
            }

            var scaleTimes = Times.ToList();
            var minScales = scales;
            RemoveTrivialFrames(ref scaleTimes, ref minScales);
            if (minScales.Count == 1)
            {
                var scaleCurve = new DaConstant32f();
                scaleCurve.CurveDataHeader_DaConstant32f = new CurveDataHeader { Format = (int)CurveFormat.DaConstant32f, Degree = 2 };
                var m = minScales[0];
                scaleCurve.Controls = new List<float>
                {
                    m[0, 0], m[0, 1], m[0, 2],
                    m[1, 0], m[1, 1], m[1, 2],
                    m[2, 0], m[2, 1], m[2, 2]
                };
                track.ScaleShearCurve = new AnimationCurve { CurveData = scaleCurve };
            }
            else
            {
                var scaleCurve = new DaK32fC32f();
                scaleCurve.CurveDataHeader_DaK32fC32f = new CurveDataHeader { Format = (int)CurveFormat.DaK32fC32f, Degree = 2 };
                scaleCurve.SetKnots(scaleTimes);
                scaleCurve.SetMatrices(minScales);
                track.ScaleShearCurve = new AnimationCurve { CurveData = scaleCurve };
            }

            return track;
        }
    }
}
