﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using System.IO;

namespace AtmosphereAutopilot
{
	/// <summary>
    /// Controls angular acceleration. Meant to be created from AngularVelAdaptiveController
	/// </summary>
	class AngularAccAdaptiveController : AdaptivePIDController
	{
		public const int PITCH = 0;
		public const int ROLL = 1;
		public const int YAW = 2;

		protected int axis;

		InstantControlModel model;
        MediumFlightModel m_model;

        PIDAccumulating pidacc;

        StreamWriter errorWriter, controlWriter, v_writer, dv_writer;

		/// <summary>
		/// Create controller instance.
		/// </summary>
		/// <param name="vessel">Vessel to control</param>
		/// <param name="module_name">Name of controller</param>
		/// <param name="wnd_id">unique for types window id</param>
		/// <param name="axis">Pitch = 0, roll = 1, yaw = 2</param>
		/// <param name="model">Flight model instance for adaptive control</param>
        /// <param name="parent">AngularVelAdaptiveController wich uses this instance as a child</param>
        public AngularAccAdaptiveController(Vessel vessel, string module_name,
            int wnd_id, int axis, InstantControlModel model, MediumFlightModel m_model)
			: base(vessel, module_name, wnd_id, new PIDAccumulating())
		{
			this.axis = axis;
			this.model = model;
            this.m_model = m_model;
            pidacc = (pid as PIDAccumulating);
		}

		protected override void OnActivate()
		{
            pid.clear();
            errorWriter = File.CreateText("D:/Games/Kerbal Space Program 0.90/Resources/" + module_name + "_telemetry_error.csv");
            controlWriter = File.CreateText("D:/Games/Kerbal Space Program 0.90/Resources/" + module_name + "_telemetry_control.csv");
            v_writer = File.CreateText("D:/Games/Kerbal Space Program 0.90/Resources/" + module_name + "_telemetry_v.csv");
            dv_writer = File.CreateText("D:/Games/Kerbal Space Program 0.90/Resources/" + module_name + "_telemetry_dv.csv");
		}

        protected override void OnDeactivate()
        {
            errorWriter.Close();
            controlWriter.Close();
            v_writer.Close();
            dv_writer.Close();
        }

        public override void ApplyControl(FlightCtrlState cntrl)
        {
            throw new NotImplementedException();
        }

        // Accumulator persistance section, tries to save accumulator between user input sessions
        bool user_is_controlling = false;
        double last_aoa = 0.0;
        double last_speed = 0.0;

		/// <summary>
		/// Main control function
		/// </summary>
		/// <param name="cntrl">Control state to change</param>
        /// <param name="target_value">Desired angular acceleration</param>
		public override double ApplyControl(FlightCtrlState cntrl, double target_value)
		{
            input = model.angular_dv[axis].getLast();
            error = target_value - input;
            desired_acc = target_value;

            errorWriter.Write(error.ToString("G8") + ',');
            v_writer.Write(model.angular_v[axis].getLast().ToString("G8") + ',');
            dv_writer.Write(model.angular_dv[axis].getLast().ToString("G8") + ',');

            double auth = k_auth;
            if (auth > 0.05)
            {
                // authority is meaningfull value
                // adapt KP
                pid.KP = apply_with_inertia(pid.KP, kp_koeff / auth + kp_offset, pid_coeff_inertia);
                // adapt KD
                pid.KD = apply_with_inertia(pid.KD, kp_kd_ratio * pid.KP + kd_offset, pid_coeff_inertia);
            }

            if (integral_fill_time > 1e-3 && small_value > 1e-3)
            {
                pid.IntegralClamp = small_value;
                pid.AccumulatorClamp = pid.IntegralClamp * integral_fill_time;
                pid.AccumulDerivClamp = pid.AccumulatorClamp / 3.0 / integral_fill_time;
                pid.KI = ki_koeff / pid.AccumulatorClamp;
                // clamp gain on small errors
                pid.IntegralGain = Common.Clamp(i_gain + Math.Abs(error) / (small_value - i_gain), 1.0);
                // deriv
                current_d2v = pid.InputDerivative;
                if (current_d2v * error > 0.0)
                {
                    // clamp gain to prevent integral overshooting
                    double reaction_deriv = small_value / integral_fill_time;
                    pid.IntegralGain = 
                        Common.Clamp(pid.IntegralGain * (1 - i_overshoot_gain * Math.Abs(current_d2v) / reaction_deriv), 0.0, 1.0);
                }
            }

            if (is_user_handling(cntrl))
            {
                // user is flying
                double raw_output = get_user_input(cntrl);
                // apply dampener output
                double dampening = -user_dampening * pid.KP * input;
                raw_output = raw_output + dampening;
                output = smooth_and_clamp(raw_output);
                set_output(cntrl, output);
                if (!user_is_controlling)
				{
					// save macro parameters for accumulator conservance
					if (axis == PITCH)
						last_aoa = m_model.aoa_pitch.getLast();
					if (axis == YAW)
						last_aoa = m_model.aoa_yaw.getLast();
					last_speed = vessel.srfSpeed;
					user_is_controlling = true;
				};
                controlWriter.Write(output.ToString("G8") + ',');
                return output;
            }
            else
                if (user_is_controlling)	// return part of accumulator
                {
                    user_is_controlling = false;
                    double accumul_persistance_k = 1.0;
                    if (axis == PITCH)
                        accumul_persistance_k *= Common.Clamp(
                            1 - Math.Abs(last_aoa - m_model.aoa_pitch.getLast()) / 0.05, 0.0, 1.0);
                    if (axis == YAW)
                        accumul_persistance_k *= Common.Clamp(
                            1 - Math.Abs(last_aoa - m_model.aoa_yaw.getLast()) / 0.05, 0.0, 1.0);
                    accumul_persistance_k *= Common.Clamp(1 - Math.Abs(vessel.srfSpeed - last_speed) / 50.0, 0.0, 1.0);
                    pid.Accumulator *= accumul_persistance_k;
                }

            current_raw = pid.Control(input, target_value, TimeWarp.fixedDeltaTime);

            proport = error * pid.KP;
            integr = pid.Accumulator * pid.KI;
            deriv = -pid.KD * pid.InputDerivative;

            output = smooth_and_clamp(current_raw);
            controlWriter.Write(output.ToString("G8") + ',');
            set_output(cntrl, output);
            return output;
		}

        bool is_user_handling(FlightCtrlState state)
        {
            switch (axis)
            {
                case PITCH:
                    return !(state.pitch == state.pitchTrim);
                case ROLL:
                    return !(state.roll == state.rollTrim);
                case YAW:
                    return !(state.yaw == state.yawTrim);
                default:
                    return false;
            }
        }

        double get_user_input(FlightCtrlState state)
        {
            switch (axis)
            {
                case PITCH:
                    return state.pitch;
                case ROLL:
                    return state.roll;
                case YAW:
                    return state.yaw;
                default:
                    return 0.0;
            }
        }

        void set_output(FlightCtrlState state, double output)
        {
            switch (axis)
            {
                case PITCH:
                    state.pitch = (float)output;
                    break;
                case ROLL:
                    state.roll = (float)output;
                    break;
                case YAW:
                    state.yaw = (float)output;
                    break;
            }
        }

        double smooth_and_clamp(double raw)
        {
            double prev_output = model.input_buf[axis].getLast();	// get previous control input
            double smoothed = raw;
            current_raw = raw;
            double raw_d = (raw - prev_output) / TimeWarp.fixedDeltaTime;
            if (raw_d > max_output_deriv)
                smoothed = prev_output + TimeWarp.fixedDeltaTime * max_output_deriv;
            if (raw_d < -max_output_deriv)
                smoothed = prev_output - TimeWarp.fixedDeltaTime * max_output_deriv;
            return Common.Clamp(smoothed, 1.0);
        }

        [AutoGuiAttr("DEBUG error", false, "G8")]
        public double error { get; private set; }

        [AutoGuiAttr("DEBUG proport", false, "G8")]
        public double proport { get; private set; }

        [AutoGuiAttr("DEBUG integr", false, "G8")]
        public double integr { get; private set; }

        [AutoGuiAttr("DEBUG deriv", false, "G8")]
        public double deriv { get; private set; }

        [AutoGuiAttr("DEBUG current_raw", false, "G8")]
        public double current_raw { get; private set; }

        [AutoGuiAttr("DEBUG desired_dv", false, "G8")]
        public double desired_acc { get; private set; }

        [AutoGuiAttr("DEBUG current_d2v", false, "G8")]
        public double current_d2v { get; private set; }

        [AutoGuiAttr("DEBUG authority", false, "G8")]
        public double k_auth { get { return model.getDvAuthority(axis); } }

        [GlobalSerializable("max_output_deriv")]
        [AutoGuiAttr("csurface speed", true, "G6")]
        public double max_output_deriv = 10.0;	// maximum output derivative, simulates control surface reaction speed

        [GlobalSerializable("acc_val")]
        [AutoGuiAttr("Steps to accumulate", true, "G6")]
        public int acc_val
        {
            get { return pidacc.StepsToAccumulate; }
            set { pidacc.StepsToAccumulate = value; }
        }

        [GlobalSerializable("user_dampening")]
        [AutoGuiAttr("user_dampening", true, "G6")]
        public double user_dampening = 1.0;

		[GlobalSerializable("pid_coeff_inertia")]
		[AutoGuiAttr("PID inertia", true, "G6")]
		public double pid_coeff_inertia = 15.0;		// PID coeffitients inertia factor

        [GlobalSerializable("ki_koeff")]
        [AutoGuiAttr("ki_koeff", true, "G6")]
        public double ki_koeff = 0.8;	        // maximum output derivative, simulates control surface reaction speed

        [GlobalSerializable("kp_kd_ratio")]
		[AutoGuiAttr("KD/KP ratio", true, "G6")]
		public double kp_kd_ratio = 0.33;

        [GlobalSerializable("kp_offset")]
        [AutoGuiAttr("KD offset", true, "G6")]
        public double kd_offset = 0.0;

        [GlobalSerializable("kp_koeff")]
        [AutoGuiAttr("KP/Authority ratio", true, "G6")]
        public double kp_koeff = 0.75;

        [GlobalSerializable("kp_offset")]
        [AutoGuiAttr("KP offset", true, "G6")]
        public double kp_offset = 0.0;

        [GlobalSerializable("small_value")]
        [AutoGuiAttr("small value", true, "G6")]
        public double small_value = 10.0;		    // arbitrary small input value. Purely intuitive

        [GlobalSerializable("integral_fill_time")]
        [AutoGuiAttr("Integral fill time", true, "G6")]
        public double integral_fill_time = 0.1;

        [GlobalSerializable("i_gain")]
        [AutoGuiAttr("Equilibrium i_gain", true, "G6")]
        public double i_gain = 1.0;

        [GlobalSerializable("i_overshoot_gain")]
        [AutoGuiAttr("Integral overshoot gain", true, "G6")]
        public double i_overshoot_gain = 1.0;
	}

    class PitchAngularAccController : AngularAccAdaptiveController
    {
        public PitchAngularAccController(Vessel vessel, InstantControlModel model, MediumFlightModel mmodel)
            : base(vessel, "Adaptive elavator trimmer accel", 77821329, 0, model, mmodel)
        { }
    }

	class RollAngularAccController : AngularAccAdaptiveController
	{
		public RollAngularAccController(Vessel vessel, InstantControlModel model, MediumFlightModel mmodel)
			: base(vessel, "Adaptive roll trimmer accel", 77821330, 1, model, mmodel)
		{ }
	}

	class YawAngularAccController : AngularAccAdaptiveController
	{
		public YawAngularAccController(Vessel vessel, InstantControlModel model, MediumFlightModel mmodel)
			: base(vessel, "Adaptive yaw trimmer accel", 77821331, 2, model, mmodel)
		{ }
	}

}
