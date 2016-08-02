﻿using System;
using System.Collections.Generic;
using Sandbox.Game.Entities;
using Sandbox.ModAPI;
using VRage;
using VRage.Collections;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRageMath;

namespace Rynchodon
{

	/// <summary>
	/// <para>Creates a List of every occupied cell for a grid. This List is used to create projections of the grid.</para>
	/// <para>This class only ever uses local positions.</para>
	/// </summary>
	/// TODO: Contains() based comparison of rejected cells
	public class GridShapeProfiler
	{

		///// <summary>Added to required distance when not landing</summary>
		//private const float NotLandingBuffer = 5f;

		private Logger m_logger = new Logger(null, "GridShapeProfiler");
		private IMyCubeGrid m_grid;
		private GridCellCache m_cellCache;
		private Vector3 m_centreRejection;
		private Vector3 m_directNorm;
		private readonly MyUniqueList<Vector3> m_rejectionCells = new MyUniqueList<Vector3>();
		private readonly FastResourceLock m_lock_rejcectionCells = new FastResourceLock();
		private bool m_landing;

		public Capsule Path { get; private set; }

		private Vector3 Centre { get { return m_grid.LocalAABB.Center; } }

		public GridShapeProfiler() { }

		public void Init(IMyCubeGrid grid, RelativePosition3F destination, Vector3 navBlockLocalPosition, bool landing)
		{
			if (grid != m_grid)
			{
				this.m_grid = grid;
				this.m_logger = new Logger("GridShapeProfiler", () => m_grid.DisplayName);
				this.m_cellCache = GridCellCache.GetCellCache(grid);
			}

			m_directNorm = Vector3.Normalize(destination.ToLocal() - navBlockLocalPosition);
			m_landing = landing;
			Vector3 centreDestination = destination.ToLocal() + Centre - navBlockLocalPosition;
			rejectAll();
			createCapsule(centreDestination, navBlockLocalPosition);
		}

		/// <summary>
		/// Rejection test for intersection with the profiled grid.
		/// </summary>
		/// <param name="grid">Grid whose cells will be rejected and compared to the profiled grid's rejections.</param>
		/// <returns>True iff there is a collision</returns>
		public bool rejectionIntersects(IMyCubeGrid grid, IMyCubeBlock ignore, out MyEntity entity, out Vector3? pointOfObstruction)
		{
			m_logger.debugLog(m_grid == null, "m_grid == null", Logger.severity.FATAL);

			//m_logger.debugLog("testing grid: " + grid.getBestName(), "rejectionIntersects()");

			GridCellCache gridCache = GridCellCache.GetCellCache(grid);
			MatrixD toLocal = m_grid.WorldMatrixNormalizedInv;
			Line pathLine = Path.get_Line();

			float minDist = m_grid.GridSize + grid.GridSize;
			//if (!m_landing)
			//	minDist += NotLandingBuffer;
			float pathRadius = Path.Radius + minDist;
			float minDistSquared = minDist * minDist;

			MyEntity entity_in = null;
			Vector3? pointOfObstruction_in = null;
			using (m_lock_rejcectionCells.AcquireSharedUsing())
				gridCache.ForEach(cell => {
					Vector3 world = grid.GridIntegerToWorld(cell);
					//m_logger.debugLog("checking position: " + world, "rejectionIntersects()");
					if (pathLine.PointInCylinder(pathRadius, world))
					{
						//m_logger.debugLog("point in cylinder: " + world, "rejectionIntersects()");
						Vector3 local = Vector3.Transform(world, toLocal);
						if (rejectionIntersects(local, minDistSquared))
						{
							entity_in = grid.GetCubeBlock(cell).FatBlock as MyEntity ?? grid as MyEntity;
							if (ignore != null && entity_in == ignore)
								return false;

							pointOfObstruction_in = pathLine.ClosestPoint(world);
							return true;
						}
					}
					return false;
				});

			if (pointOfObstruction_in.HasValue)
			{
				entity = entity_in;
				pointOfObstruction = pointOfObstruction_in;
				return true;
			}

			entity = null;
			pointOfObstruction = null;
			return false;
		}

		/// <summary>
		/// Rejection test for intersection with the profiled grid.
		/// </summary>
		/// <param name="localMetresPosition">The local position in metres.</param>
		/// <returns>true if the rejection collides with one or more of the grid's rejections</returns>
		private bool rejectionIntersects(Vector3 localMetresPosition, float minDistSquared)
		{
			Vector3 TestRejection = RejectMetres(localMetresPosition);
			foreach (Vector3 ProfileRejection in m_rejectionCells)
			{
				//m_logger.debugLog("distance between: " + Vector3.DistanceSquared(TestRejection, ProfileRejection), "rejectionIntersects()");
				if (Vector3.DistanceSquared(TestRejection, ProfileRejection) < minDistSquared)
					return true;
			}
			return false;
		}

		private Vector3 RejectMetres(Vector3 metresPosition)
		{ return Vector3.Reject(metresPosition, m_directNorm); }

		/// <summary>
		/// Perform a vector rejection of every occupied cell from DirectionNorm and store the results in rejectionCells.
		/// </summary>
		private void rejectAll()
		{
			using (m_lock_rejcectionCells.AcquireExclusiveUsing())
			{
				m_rejectionCells.Clear();

				m_centreRejection = RejectMetres(Centre);
				m_cellCache.ForEach(cell => {
					Vector3 rejection = RejectMetres(cell * m_grid.GridSize);
					m_rejectionCells.Add(rejection);
				});
			}
		}

		/// <param name="centreDestination">where the centre of the grid will end up (local)</param>
		private void createCapsule(Vector3 centreDestination, Vector3 localPosition)
		{
			float longestDistanceSquared = 0;
			using (m_lock_rejcectionCells.AcquireSharedUsing())
				foreach (Vector3 rejection in m_rejectionCells)
				{
					float distanceSquared = (rejection - m_centreRejection).LengthSquared();
					if (distanceSquared > longestDistanceSquared)
						longestDistanceSquared = distanceSquared;
				}
			Vector3 P0 = RelativePosition3F.FromLocal(m_grid, Centre).ToWorld();

			//Vector3D P1;
			//if (m_landing)
			//{
			Vector3 P1 = RelativePosition3F.FromLocal(m_grid, centreDestination).ToWorld();
			//}
			//else
			//{
			//	//// extend capsule past destination by distance between remote and front of grid
			//	//Ray navTowardsDest = new Ray(localPosition, m_directNorm);
			//	//float tMin, tMax;
			//	//m_grid.LocalVolume.IntersectRaySphere(navTowardsDest, out tMin, out tMax);
			//	//P1 = RelativeVector3F.createFromLocal(centreDestination + tMax * m_directNorm, m_grid).getWorldAbsolute();

			//	// extend capsule by length of grid
			//	P1 = RelativePosition3F.FromLocal(m_grid, centreDestination + m_directNorm * m_grid.GetLongestDim()).ToWorld();
			//}

			float CapsuleRadius = (float)Math.Sqrt(longestDistanceSquared) + 3f * m_grid.GridSize;// +(m_landing ? 0f : NotLandingBuffer);
			Path = new Capsule(P0, P1, CapsuleRadius);

			m_logger.debugLog("Path capsule created from " + P0 + " to " + P1 + ", radius: " + CapsuleRadius);
		}

	}
}
