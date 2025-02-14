﻿using System;
using System.Collections.Generic;
using AAEmu.Commons.Network;
using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Funcs;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Tasks.Doodads;
using NLog;

namespace AAEmu.Game.Models.Game.DoodadObj
{
    public class Doodad : BaseUnit
    {
        private static Logger _log = LogManager.GetCurrentClassLogger();
        private float _scale;
        private int _data;
        public uint TemplateId { get; set; }
        public uint DbId { get; set; }
        public bool IsPersistent { get; set; } = false;
        public DoodadTemplate Template { get; set; }
        public override float Scale => _scale;
        public uint FuncGroupId { get; set; }
        public ulong ItemId { get; set; }
        public ulong UccId { get; set; }
        public uint ItemTemplateId { get; set; }
        public DateTime GrowthTime { get; set; }
        public DateTime PlantTime { get; set; }
        public uint OwnerId { get; set; }
        public uint OwnerObjId { get; set; }
        public uint ParentObjId { get; set; }
        public DoodadOwnerType OwnerType { get; set; }
        public AttachPointKind AttachPoint { get; set; }
        public uint DbHouseId { get; set; }

        public int Data
        {
            get => _data;
            set
            {
                if (value != _data)
                {
                    _data = value;
                    if (DbId > 0)
                        Save();
                }
            }
        }

        public uint QuestGlow { get; set; } //0 off // 1 on
        public DoodadSpawner Spawner { get; set; }
        public DoodadFuncTask FuncTask { get; set; }
        public uint TimeLeft => GrowthTime > DateTime.UtcNow ? (uint)(GrowthTime - DateTime.UtcNow).TotalMilliseconds : 0; // TODO formula time of phase
        public bool ToPhaseAndUse { get; set; }
        public int PhaseRatio { get; set; }
        public int CumulativePhaseRatio { get; set; }
        public uint CurrentPhaseId { get; set; }
        public uint OverridePhase { get; set; }
        private bool _deleted = false;
        public VehicleSeat Seat { get; set; }
        
        public Doodad()
        {
            _scale = 1f;
            PlantTime = DateTime.MinValue;
            AttachPoint = AttachPointKind.System;
            Seat = new VehicleSeat(this);
        }

        public void SetScale(float scale)
        {
            _scale = scale;
        }

        public void SetData(int data)
        {
            _data = data;
        }

        // public void DoFirstPhase(Unit unit)
        // {
        //     
        // }

        /// <summary>
        /// This "uses" the doodad. Using a doodad means running its functions in doodad_funcs
        /// </summary>
        public void Use(Unit unit, uint skillId, uint recursionDepth = 0)
        {
            recursionDepth++;
            if (recursionDepth % 10 == 0)
                _log.Warn("Doodad {0} (TemplateId {1}) might be looping indefinitely. {2} recursionDepth.", ObjId, TemplateId, recursionDepth);
            _log.Trace("Using phase {0}", CurrentPhaseId);
            
            // Get all doodad_funcs
            var funcs = DoodadManager.Instance.GetFuncsForGroup(CurrentPhaseId);

            // Apply them
            var nextFunc = 0;
            var isUse = false;
            ToPhaseAndUse = false;

            try
            {
                foreach (var func in funcs)
                {
                    if ((func.SkillId <= 0 || func.SkillId != skillId) && func.SkillId != 0)
                        continue;
                    
                    func.Use(unit, this, skillId, func.NextPhase);

                    if (ToPhaseAndUse)
                    {
                        isUse = true;
                        nextFunc = func.NextPhase;
                    }

                    break;
                }
            }
            catch (Exception e)
            {
                _log.Fatal(e, "Doodad func crashed !");
            }

            if (nextFunc == 0)
                return;

            if (isUse)
                GoToPhaseAndUse(unit, nextFunc, skillId, recursionDepth);
            else
                GoToPhase(unit, nextFunc);
        }

        /// <summary>
        /// This executes a doodad's phase. Phase functions start as soon as the doodad switches to a new phase.
        /// </summary>
        public void DoPhase(Unit unit, uint skillId, uint recursionDepth = 0)
        {
            recursionDepth++;
            if (recursionDepth % 10 == 0)
                _log.Warn("Doodad {0} (TemplateId {1}) might be phasing indefinitely. {2} recursionDepth.", ObjId, TemplateId, recursionDepth);

            _log.Trace("Doing phase {0}", CurrentPhaseId);
            var phaseFuncs = DoodadManager.Instance.GetPhaseFunc(CurrentPhaseId);

            OverridePhase = 0;
            try
            {
                foreach (var phaseFunc in phaseFuncs)
                {
                    phaseFunc.Use(unit, this, skillId);
                    if (OverridePhase > 0)
                        break;
                }
            }
            catch (Exception e)
            {
                _log.Fatal(e, "Doodad phase crashed!");
            }

            if (OverridePhase > 0)
            {
                CurrentPhaseId = OverridePhase;
                DoPhase(unit, skillId, recursionDepth);
            }

            if (!_deleted)
                Save();
        }

        /// <summary>
        /// Changes the doodad's phase
        /// </summary>
        /// <param name="unit">Unit who triggered the change</param>
        /// <param name="funcGroupId">New phase to go to</param>
        public void GoToPhase(Unit unit, int funcGroupId, uint skillId = 0)
        {
            _log.Trace("Going to phase {0}", funcGroupId);
            if (funcGroupId == -1)
            {
                // Delete doodad
                Delete();
            }
            else
            {
                CurrentPhaseId = (uint)funcGroupId;
                PhaseRatio = Rand.Next(0, 10000);
                CumulativePhaseRatio = 0;
                DoPhase(unit, skillId);

                _log.Debug("SCDoodadPhaseChangedPacket : CurrentPhaseId {0}", CurrentPhaseId);
                BroadcastPacket(new SCDoodadPhaseChangedPacket(this), true);
            }
        }

        public void GoToPhaseAndUse(Unit unit, int funcGroupId, uint skillId, uint recursionDepth = 0)
        {
            recursionDepth++;
            _log.Trace("Going to phase {0} and using it", funcGroupId);
            if (funcGroupId == -1)
            {
                // Delete doodad
                Delete();
            }
            else
            {
                CurrentPhaseId = (uint)funcGroupId;
                PhaseRatio = Rand.Next(0, 10000);
                CumulativePhaseRatio = 0;
                DoPhase(unit, skillId);

                _log.Debug("SCDoodadPhaseChangedPacket : CurrentPhaseId {0}", CurrentPhaseId);
                BroadcastPacket(new SCDoodadPhaseChangedPacket(this), true);

                Use(unit, skillId, recursionDepth);
            }
        }

        public List<uint> GetStartFuncs()
        {
            var startGroupIds = new List<uint>();
            foreach (var funcGroup in Template.FuncGroups)
            {
                if (funcGroup.GroupKindId == DoodadFuncGroups.DoodadFuncGroupKind.Start)
                    startGroupIds.Add(funcGroup.Id);
            }

            return startGroupIds;
        }

        public uint GetFuncGroupId()
        {
            foreach (var funcGroup in Template.FuncGroups)
            {
                if (funcGroup.GroupKindId == DoodadFuncGroups.DoodadFuncGroupKind.Start)
                    return funcGroup.Id;
            }
            return 0;
        }

        public void OnSkillHit(Unit caster, uint skillId)
        {
            var funcs = DoodadManager.Instance.GetFuncsForGroup(CurrentPhaseId);
            foreach (var func in funcs)
            {
                if (func.FuncType == "DoodadFuncSkillHit")
                {
                    Use(null, skillId);
                }
            }
        }

        public override void AddVisibleObject(Character character)
        {
            character.SendPacket(new SCDoodadCreatedPacket(this));
            base.AddVisibleObject(character);
        }

        public override void RemoveVisibleObject(Character character)
        {
            base.RemoveVisibleObject(character);
            character.SendPacket(new SCDoodadRemovedPacket(ObjId));
        }

        public PacketStream Write(PacketStream stream)
        {
            stream.WriteBc(ObjId); //The object # in the list
            stream.Write(TemplateId); //The template id needed for that object, the client then uses the template configurations, not the server
            stream.WriteBc(OwnerObjId); //The creator of the object
            stream.WriteBc(ParentObjId); //Things like boats or cars,
            stream.Write((byte)AttachPoint); // attachPoint, relative to the parentObj (Door or window on a house, seats on carriage, etc.)
            if ((AttachPoint > 0) || (ParentObjId > 0))
            {
                stream.WritePosition(Transform.Local.Position.X, Transform.Local.Position.Y, Transform.Local.Position.Z);
                var (roll, pitch, yaw) = Transform.Local.ToRollPitchYawShorts();
                stream.Write(roll);
                stream.Write(pitch);
                stream.Write(yaw);
            }
            else
            {
                stream.WritePosition(Transform.World.Position.X, Transform.World.Position.Y, Transform.World.Position.Z);
                var(roll, pitch, yaw) = Transform.World.ToRollPitchYawShorts();
                stream.Write(roll);
                stream.Write(pitch);
                stream.Write(yaw);
            }

            stream.Write(Scale); //The size of the object
            stream.Write(false); // hasLootItem
            stream.Write(CurrentPhaseId); // doodad_func_group_id
            stream.Write(OwnerId); // characterId (Database relative)
            stream.Write(UccId);
            stream.Write(ItemTemplateId);
            stream.Write(0u); //??type2
            stream.Write(TimeLeft); // growing
            stream.Write(PlantTime); //Time stamp of when it was planted
            stream.Write(QuestGlow); //When this is higher than 0 it shows a blue orb over the doodad
            stream.Write(0); // family TODO
            stream.Write(-1); // puzzleGroup /for instances maybe?
            stream.Write((byte)OwnerType); // ownerType
            stream.Write(DbHouseId); // dbHouseId
            stream.Write(Data); // data

            return stream;
        }
        
        public override void Delete()
        {
            base.Delete();
            _deleted = true;

            if (DbId > 0)
            {
                using (var connection = MySQL.CreateConnection())
                {
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "DELETE FROM doodads WHERE id = @id";
                        command.Parameters.AddWithValue("@id", DbId);
                        command.Prepare();
                        command.ExecuteNonQuery();
                    }
                }
            }
        }

        public void Save()
        {
            if (!IsPersistent)
                return;
            DbId = DbId > 0 ? DbId : DoodadIdManager.Instance.GetNextId();
            using (var connection = MySQL.CreateConnection())
            {
                using (var command = connection.CreateCommand())
                {
                    // Lookup Parent
                    var parentDoodadId = 0u;
                    if ((Transform?.Parent?.GameObject is Doodad pDoodad) && (pDoodad.DbId > 0))
                        parentDoodadId = pDoodad.DbId;

                    command.CommandText = 
                        "REPLACE INTO doodads (`id`, `owner_id`, `owner_type`, `template_id`, `current_phase_id`, `plant_time`, `growth_time`, `phase_time`, `x`, `y`, `z`, `roll`, `pitch`, `yaw`, `item_id`, `house_id`, `parent_doodad`, `item_template_id`, `item_container_id`, `data`) " +
                        "VALUES(@id, @owner_id, @owner_type, @template_id, @current_phase_id, @plant_time, @growth_time, @phase_time, @x, @y, @z, @roll, @pitch, @yaw, @item_id, @house_id, @parent_doodad, @item_template_id, @item_container_id, @data)";
                    command.Parameters.AddWithValue("@id", DbId);
                    command.Parameters.AddWithValue("@owner_id", OwnerId);
                    command.Parameters.AddWithValue("@owner_type", OwnerType);
                    command.Parameters.AddWithValue("@template_id", TemplateId);
                    command.Parameters.AddWithValue("@current_phase_id", CurrentPhaseId);
                    command.Parameters.AddWithValue("@plant_time", PlantTime);
                    command.Parameters.AddWithValue("@growth_time", GrowthTime);
                    command.Parameters.AddWithValue("@phase_time", DateTime.MinValue);
                    // We save it's world position, and upon loading, we re-parent things depending on the data
                    command.Parameters.AddWithValue("@x", Transform?.World.Position.X ?? 0f);
                    command.Parameters.AddWithValue("@y", Transform?.World.Position.Y ?? 0f);
                    command.Parameters.AddWithValue("@z", Transform?.World.Position.Z ?? 0f);
                    command.Parameters.AddWithValue("@roll", Transform?.World.Rotation.X ?? 0f);
                    command.Parameters.AddWithValue("@pitch", Transform?.World.Rotation.Y ?? 0f);
                    command.Parameters.AddWithValue("@yaw", Transform?.World.Rotation.Z ?? 0f);
                    command.Parameters.AddWithValue("@item_id", ItemId);
                    command.Parameters.AddWithValue("@house_id", DbHouseId);
                    command.Parameters.AddWithValue("@parent_doodad", parentDoodadId);
                    command.Parameters.AddWithValue("@item_template_id", ItemTemplateId);
                    command.Parameters.AddWithValue("@item_container_id", GetItemContainerId());
                    command.Parameters.AddWithValue("@data", Data);
                    command.Prepare();
                    command.ExecuteNonQuery();
                }
            }
        }

        public override bool AllowRemoval()
        {
            // Only allow removal if there is no other persistent Doodads stacked on top of this
            foreach (var child in Transform.Children)
            {
                if ((child.GameObject is Doodad doodad) && (doodad.DbId > 0))
                    return false;
            }
            
            return base.AllowRemoval();
        }

        /// <summary>
        /// Return the associated ItemContainerId for this Doodad
        /// </summary>
        /// <returns></returns>
        public virtual ulong GetItemContainerId()
        {
            return 0;
        }
        
    }
}
