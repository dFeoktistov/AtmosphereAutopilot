﻿/*
Atmosphere Autopilot, plugin for Kerbal Space Program.
Copyright (C) 2015-2016, Baranin Alexander aka Boris-Barboris.

Atmosphere Autopilot is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.
Atmosphere Autopilot is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.
You should have received a copy of the GNU General Public License
along with Atmosphere Autopilot.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AtmosphereAutopilot
{

    /// <summary>
    /// Synchronised ModuleControlSurface implementation, greatly simplifies control and flight model regression
    /// by making all control surfaces move in one phase.
    /// </summary>
    public class SyncModuleControlSurface: ModuleControlSurface
    {
        public const float CSURF_SPD = 2.0f;

        // normalized [-1.0, 1.0] previous actions for separate axes
        protected float prev_pitch_normdeflection = 0.0f;
        protected float prev_roll_normdeflection = 0.0f;
        protected float prev_yaw_normdeflection = 0.0f;

        protected bool was_deployed = false;

        public override void OnStart(PartModule.StartState state)
        {
            base.OnStart(state);
            if (HighLogic.LoadedSceneIsFlight)
            {
                if (!usesMirrorDeploy)
                {
                    usesMirrorDeploy = true;
                    mirrorDeploy = false;
                    if (part.symMethod == SymmetryMethod.Mirror &&
                        part.symmetryCounterparts != null &&
                        part.symmetryCounterparts.Count > 0)
                    {
                        Part p = part.symmetryCounterparts[0];
                        if (Mathf.Abs(part.transform.localRotation.w) < Mathf.Abs(p.transform.localRotation.w))
                        {
                            this.mirrorDeploy = true;
                        }
                        else if (Mathf.Abs(part.transform.localRotation.w) == Mathf.Abs(p.transform.localRotation.w)
                            && part.transform.localRotation.x < p.transform.localRotation.x)
                        {
                            this.mirrorDeploy = true;
                        }
                    }
                }
            }
        }

        protected override void CtrlSurfaceUpdate(Vector3 vel)
        {
            if (vessel.transform == null)
                return;

            this.alwaysRecomputeLift = true;

            Vector3 world_com = vessel.CoM;
            float pitch_input = ignorePitch ? 0.0f : vessel.ctrlState.pitch;
            float roll_input = ignoreRoll ? 0.0f : vessel.ctrlState.roll;
            float yaw_input = ignoreYaw ? 0.0f : vessel.ctrlState.yaw;

            if (base.vessel.atmDensity == 0.0)
                pitch_input = roll_input = yaw_input = 0.0f;

            float spd_factor = TimeWarp.fixedDeltaTime * CSURF_SPD;
            float fwd_airstream_factor = Mathf.Sign(Vector3.Dot(vessel.ReferenceTransform.up, vessel.srf_velocity) + 0.1f);
            float exp_spd_factor = 0.0f;
            if (useExponentialSpeed)
                exp_spd_factor = actuatorSpeed / actuatorSpeedNormScale *
                    TimeWarp.fixedDeltaTime;

            if (deploy)
            {
                float normdeflection = deflection / deployAngle;
                if (float.IsNaN(normdeflection))
                    normdeflection = 0.0f;
                float target = deployInvert ? 1.0f : -1.0f;
                target *= partDeployInvert ? -1.0f : 1.0f;
                if (usesMirrorDeploy)
                    if (mirrorDeploy)
                        target *= -1.0f;
                if (!ignorePitch)
                    prev_pitch_normdeflection = target;
                if (!ignoreRoll)
                    prev_roll_normdeflection = target;
                if (!ignoreYaw)
                    prev_yaw_normdeflection = target;
                was_deployed = true;
                action = deployAngle * target;
                deflection = deflection + deployAngle * Common.Clampf(target - normdeflection, spd_factor);
                ctrlSurface.localRotation = Quaternion.AngleAxis(deflection, Vector3.right) * neutral;
            }
            else
            {
                if (!ignorePitch)
                {
                    float axis_factor = Vector3.Dot(vessel.ReferenceTransform.right, baseTransform.right) * fwd_airstream_factor;
                    float pitch_factor = axis_factor * Math.Sign(Vector3.Dot(world_com - baseTransform.position, vessel.ReferenceTransform.up));
                    if (was_deployed)
                        prev_pitch_normdeflection = Common.Clampf(prev_pitch_normdeflection, Mathf.Abs(pitch_factor));
                    float new_pitch_action = pitch_input * pitch_factor;
                    if (useExponentialSpeed)
                        prev_pitch_normdeflection = Mathf.Lerp(prev_pitch_normdeflection, new_pitch_action, exp_spd_factor);
                    else
                        prev_pitch_normdeflection = prev_pitch_normdeflection + Common.Clampf(new_pitch_action - prev_pitch_normdeflection, spd_factor * Math.Abs(axis_factor));
                }
                else
                    prev_pitch_normdeflection = 0.0f;

                if (!ignoreRoll)
                {
                    float axis_factor = Vector3.Dot(vessel.ReferenceTransform.up, baseTransform.up) * fwd_airstream_factor;
                    float roll_factor = axis_factor * Math.Sign(Vector3.Dot(vessel.ReferenceTransform.up,
                        Vector3.Cross(world_com - baseTransform.position, baseTransform.forward)));
                    if (was_deployed)
                        prev_roll_normdeflection = Common.Clampf(prev_roll_normdeflection, Mathf.Abs(roll_factor));
                    float new_roll_action = roll_input * roll_factor;
                    if (useExponentialSpeed)
                        prev_roll_normdeflection = Mathf.Lerp(prev_roll_normdeflection, new_roll_action, exp_spd_factor);
                    else
                        prev_roll_normdeflection = prev_roll_normdeflection + Common.Clampf(new_roll_action - prev_roll_normdeflection, spd_factor * axis_factor);
                }
                else
                    prev_roll_normdeflection = 0.0f;

                if (!ignoreYaw)
                {
                    float axis_factor = Vector3.Dot(vessel.ReferenceTransform.forward, baseTransform.right) * fwd_airstream_factor;
                    float yaw_factor = axis_factor * Math.Sign(Vector3.Dot(world_com - baseTransform.position, vessel.ReferenceTransform.up));
                    if (was_deployed)
                        prev_yaw_normdeflection = Common.Clampf(prev_yaw_normdeflection, Mathf.Abs(yaw_factor));
                    float new_yaw_action = yaw_input * yaw_factor;
                    if (useExponentialSpeed)
                        prev_yaw_normdeflection = Mathf.Lerp(prev_yaw_normdeflection, new_yaw_action, exp_spd_factor);
                    else
                        prev_yaw_normdeflection = prev_yaw_normdeflection + Common.Clampf(new_yaw_action - prev_yaw_normdeflection, spd_factor * Math.Abs(axis_factor));
                }
                else
                    prev_yaw_normdeflection = 0.0f;

                was_deployed = false;
                deflection = action = ctrlSurfaceRange * authorityLimiter * 0.01f *
                    Common.Clampf(prev_pitch_normdeflection + prev_roll_normdeflection + prev_yaw_normdeflection, 1.0f);
                ctrlSurface.localRotation = Quaternion.AngleAxis(deflection, Vector3.right) * neutral;
            }
        }
    }
}
