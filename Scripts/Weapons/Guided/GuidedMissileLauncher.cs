﻿using System;
using System.Collections.Generic;
using Rynchodon.AntennaRelay;
using Sandbox.Game.Weapons;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;
using Ingame = Sandbox.ModAPI.Ingame;

namespace Rynchodon.Weapons.Guided
{
	public class GuidedMissileLauncher
	{
		private const ulong checkInventoryInterval = Globals.UpdatesPerSecond;

		#region Static

		private static Logger staticLogger = new Logger("GuidedMissileLauncher");

		static GuidedMissileLauncher()
		{
			MyAPIGateway.Entities.OnEntityAdd += Entities_OnEntityAdd;
			MyAPIGateway.Entities.OnCloseAll += Entities_OnCloseAll;
		}

		private static void Entities_OnCloseAll()
		{
			MyAPIGateway.Entities.OnEntityAdd -= Entities_OnEntityAdd;
			MyAPIGateway.Entities.OnCloseAll -= Entities_OnCloseAll;
			staticLogger = null;
		}

		public static bool IsGuidedMissileLauncher(IMyCubeBlock block)
		{
			return block is Ingame.IMyUserControllableGun && WeaponDescription.GetFor(block).GuidedMissileLauncher;
		}

		private static void Entities_OnEntityAdd(IMyEntity obj)
		{
			if (obj is MyAmmoBase && obj.ToString().StartsWith("MyMissile"))
			{
				Registrar.ForEach((GuidedMissileLauncher launcher) => {
					return launcher.MissileBelongsTo(obj);
				});
			}
		}

		#endregion

		private readonly Logger myLogger;
		public readonly WeaponTargeting m_weaponTarget;
		public IMyCubeBlock CubeBlock { get { return m_weaponTarget.CubeBlock; } }
		public IMyFunctionalBlock FuncBlock { get { return CubeBlock as IMyFunctionalBlock; } }
		/// <summary>Local position where the magic happens (hopefully).</summary>
		private readonly BoundingBox MissileSpawnBox;
		private readonly MyInventoryBase myInventory;
		public readonly NetworkClient m_netClient;

		private ulong nextCheckInventory;
		private MyFixedPoint prev_mass;
		private MyFixedPoint prev_volume;
		public Ammo loadedAmmo { get; private set; }
		private List<IMyEntity> m_cluster = new List<IMyEntity>();

		private bool onCooldown;
		private TimeSpan cooldownUntil;

		public GuidedMissileLauncher(WeaponTargeting weapon)
		{
			m_weaponTarget = weapon;
			myLogger = new Logger("GuidedMissileLauncher", CubeBlock);
			if (m_weaponTarget.m_netClient != null)
				m_netClient = m_weaponTarget.m_netClient;
			else
				m_netClient = new NetworkClient(m_weaponTarget.CubeBlock);

			var defn = CubeBlock.GetCubeBlockDefinition();

			Vector3[] points = new Vector3[3];
			Vector3 forwardAdjust = Vector3.Forward * WeaponDescription.GetFor(CubeBlock).MissileSpawnForward;
			points[0] = CubeBlock.LocalAABB.Min + forwardAdjust;
			points[1] = CubeBlock.LocalAABB.Max + forwardAdjust;
			points[2] = CubeBlock.LocalAABB.Min + Vector3.Up * CubeBlock.GetCubeBlockDefinition().Size.Y * CubeBlock.CubeGrid.GridSize + forwardAdjust;

			MissileSpawnBox = BoundingBox.CreateFromPoints(points);
			if (m_weaponTarget.myTurret != null)
			{
				myLogger.debugLog("original box: " + MissileSpawnBox, "GuidedMissileLauncher()");
				MissileSpawnBox.Inflate(CubeBlock.CubeGrid.GridSize * 2f);
			}

			myLogger.debugLog("MissileSpawnBox: " + MissileSpawnBox, "GuidedMissileLauncher()");

			myInventory = ((MyEntity)CubeBlock).GetInventoryBase(0);

			Registrar.Add(weapon.FuncBlock, this);
			m_weaponTarget.GuidedLauncher = true;
		}

		public void Update1()
		{
			UpdateLoadedMissile();
			CheckCooldown();
		}

		private bool MissileBelongsTo(IMyEntity missile)
		{
			if (m_weaponTarget.myTurret == null && !(CubeBlock as Ingame.IMyUserControllableGun).IsShooting)
			{
				myLogger.debugLog("Not mine, not shooting", "MissileBelongsTo()");
				return false;
			}
			Vector3D local = Vector3D.Transform(missile.GetPosition(), CubeBlock.WorldMatrixNormalizedInv);
			if (MissileSpawnBox.Contains(local) != ContainmentType.Contains)
			{
				myLogger.debugLog("Not in my box: " + missile + ", position: " + local, "MissileBelongsTo()");
				return false;
			}
			if (m_weaponTarget.myTurret == null)
			{
				if (Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward) > 0.01)
				{
					myLogger.debugLog("Facing the wrong way: " + missile + ", missile direction: " + missile.WorldMatrix.Forward + ", block direction: " + CubeBlock.WorldMatrix.Forward
						+ ", RectangularDistance: " + Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward), "MissileBelongsTo()");
					return false;
				}
			}
			else
			{
				Vector3 turretDirection;
				Vector3.CreateFromAzimuthAndElevation(m_weaponTarget.myTurret.Azimuth, m_weaponTarget.myTurret.Elevation, out turretDirection);
				turretDirection = Vector3.Transform(turretDirection, CubeBlock.WorldMatrix.GetOrientation());
				if (Vector3D.RectangularDistance(turretDirection, missile.WorldMatrix.Forward) > 0.01)
				{
					myLogger.debugLog("Facing the wrong way: " + missile + ", missile direction: " + missile.WorldMatrix.Forward + ", turret direction: " + turretDirection
						+ ", RectangularDistance: " + Vector3D.RectangularDistance(CubeBlock.WorldMatrix.Forward, missile.WorldMatrix.Forward), "MissileBelongsTo()");
					return false;
				}
			}

			if (loadedAmmo == null)
			{
				myLogger.debugLog("Mine but no loaded ammo!", "MissileBelongsTo()", Logger.severity.INFO);
				return true;
			}

			if (loadedAmmo.Description == null || loadedAmmo.Description.GuidanceSeconds < 1f)
			{
				myLogger.debugLog("Mine but not a guided missile!", "MissileBelongsTo()", Logger.severity.INFO);
				return true;
			}

			//myLogger.debugLog("Opts: " + m_weaponTarget.Options, "MissileBelongsTo()");
			try
			{
				if (loadedAmmo.IsCluster)
				{
					if (m_cluster.Count == 0)
						FuncBlock.ApplyAction("Shoot_On");

					m_cluster.Add(missile);
					if (m_cluster.Count >= loadedAmmo.MagazineDefinition.Capacity)
					{
						myLogger.debugLog("Final missile in cluster: " + missile, "MissileBelongsTo()", Logger.severity.DEBUG);
					}
					else
					{
						myLogger.debugLog("Added to cluster: " + missile, "MissileBelongsTo()", Logger.severity.DEBUG);
						return true;
					}
				}

				myLogger.debugLog("creating new guided missile", "MissileBelongsTo()");
				if (m_cluster.Count != 0)
				{
					new GuidedMissile(new Cluster(m_cluster, CubeBlock), this);
					StartCooldown();
					m_cluster.Clear();
				}
				else
					new GuidedMissile(missile, this);
			}
			catch (Exception ex)
			{
				myLogger.alwaysLog("failed to create GuidedMissile", "MissileBelongsTo()", Logger.severity.ERROR);
				myLogger.alwaysLog("Exception: " + ex, "MissileBelongsTo()", Logger.severity.ERROR);
			}

			return true;
		}

		private void UpdateLoadedMissile()
		{
			if (myInventory.CurrentMass == prev_mass && myInventory.CurrentVolume == prev_volume && Globals.UpdateCount >= nextCheckInventory)
				return;

			nextCheckInventory = Globals.UpdateCount + checkInventoryInterval;
			prev_mass = myInventory.CurrentMass;
			prev_volume = myInventory.CurrentVolume;

			Ammo newAmmo = Ammo.GetLoadedAmmo(CubeBlock);
			if (newAmmo != null && newAmmo != loadedAmmo)
			{
				loadedAmmo = newAmmo;
				myLogger.debugLog("loaded ammo: " + loadedAmmo.AmmoDefinition, "UpdateLoadedMissile()");
			}
		}

		private void StartCooldown()
		{
			FuncBlock.RequestEnable(false);
			FuncBlock.ApplyAction("Shoot_Off");
			onCooldown = true;
			cooldownUntil = MyAPIGateway.Session.ElapsedPlayTime + TimeSpan.FromSeconds(loadedAmmo.Description.ClusterCooldown);
		}

		private void CheckCooldown()
		{
			if (!onCooldown)
				return;

			if (cooldownUntil > MyAPIGateway.Session.ElapsedPlayTime)
			{
				if (FuncBlock.Enabled)
				{
					FuncBlock.RequestEnable(false);
					FuncBlock.ApplyAction("Shoot_Off");
				}
			}
			else
			{
				myLogger.debugLog("off cooldown", "CheckCooldown()");
				onCooldown = false;
				FuncBlock.RequestEnable(true);
				// do not restore shooting toggle, makes it difficult to turn the thing off
			}
		}

	}
}
