﻿using System.Text;
using Rynchodon.Autopilot.Data;
using Rynchodon.Autopilot.Movement;
using Sandbox.ModAPI;

namespace Rynchodon.Autopilot.Navigator
{
	/// <summary>
	/// <para>Stops the ship</para>
	/// </summary>
	/// Do not try to make this manage rotation, it may have been set for a reason.
	public class Stopper : NavigatorMover
	{

		private readonly Logger _logger;
		private readonly bool m_exitAfter;

		/// <summary>
		/// Creates a new Stopper
		/// </summary>
		/// <param name="mover">The Mover to use</param>
		/// <param name="navSet">The settings to use</param>
		public Stopper(Mover mover, AllNavigationSettings navSet, bool exitAfter = false)
			: base(mover, navSet)
		{
			_logger = new Logger("Stopper", m_controlBlock.Controller);
			m_exitAfter = exitAfter;

			m_mover.StopMove();
			m_mover.StopRotate();

			m_navSet.Settings_Task_NavRot.NavigatorMover = this;
		}

		/// <summary>
		/// Waits for the grid to stop.
		/// </summary>
		public override void Move()
		{
			if (m_mover.Block.Physics.LinearVelocity.LengthSquared() == 0f && m_mover.Block.Physics.AngularVelocity.LengthSquared() == 0f)
			{
				INavigatorRotator rotator = m_navSet.Settings_Current.NavigatorRotator;
				if (rotator != null && !m_navSet.DirectionMatched())
				{
					_logger.debugLog("waiting for rotator to match", "Move()");
					return;
				}

				_logger.debugLog("stopped", "Stopper()");
				m_navSet.OnTaskComplete_NavRot();
				if (m_exitAfter)
				{
					_logger.debugLog("setting disable", "Move()", Logger.severity.DEBUG);
					m_controlBlock.SetControl(false);
				}
			}
			//else
			//	_logger.debugLog("not stopped", "Stopper()");
		}

		/// <summary>
		/// Appends "Exit after stopping" or "Stopping"
		/// </summary>
		/// <param name="customInfo">The autopilot block's custom info</param>
		public override void AppendCustomInfo(StringBuilder customInfo)
		{
			if (m_exitAfter)
				customInfo.AppendLine("Exit after stopping");
			else
				customInfo.AppendLine("Stopping");
		}

	}

}
