using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game;
using ImGuiNET;
using Status = FFXIVClientStructs.FFXIV.Client.Game.Status;

namespace NoClippy
{
    public partial class Configuration
    {
        public bool PredictStatusApplications = false;
        public bool PredictMudras = false;
        public bool PredictDualcast = false;
    }
}

namespace NoClippy.Modules
{
    public class StatusPrediction : Module
    {
        public override bool IsEnabled
        {
            get => NoClippy.Config.PredictStatusApplications || NoClippy.Config.PredictMudras || NoClippy.Config.PredictDualcast;
            set => NoClippy.Config.PredictStatusApplications = NoClippy.Config.PredictMudras = NoClippy.Config.PredictDualcast = value;
        }

        public override int DrawOrder => 15;

        private class PredictedStatusList
        {
            private readonly List<PredictedStatus> statuses = new();

            public PredictedStatus Add(ushort statusID = 0, byte stacks = 0, byte param = 0, bool replace = false, float timer = 0.75f)
            {
                var statusList = DalamudApi.ClientState.LocalPlayer!.StatusList;

                int prev;

                if (!replace)
                {
                    if (statusList.Any(status => status.StatusId == statusID)) return null;
                    prev = statuses.FindIndex(s => !s.replace && s.status.StatusID == statusID);
                }
                else
                {
                    prev = statuses.FindIndex(s => s.replace && s.status.StatusID == statusID);
                }

                if (prev >= 0)
                {
                    statuses[prev].TryRemove(statusList);
                    statuses.RemoveAt(prev);
                }

                var predicted = new PredictedStatus
                {
                    status = new Status
                    {
                        StatusID = statusID,
                        StackCount = stacks,
                        Param = param
                    },
                    timer = timer,
                    replace = replace
                };

                statuses.Add(predicted);
                return predicted;
            }

            public void Update(float dt)
            {
                var statusList = DalamudApi.ClientState.LocalPlayer?.StatusList;
                var exists = statusList != null;

                for (int i = statuses.Count - 1; i >= 0; i--)
                {
                    var status = statuses[i];
                    if ((status.timer -= dt) > 0) continue;

                    if (exists)
                        status.TryRemove(statusList);

                    statuses.RemoveAt(i);
                }
            }

            public unsafe void Apply(StatusList statusList)
            {
                if (statuses.Count == 0) return;

                var currentIndex = 0;
                for (short i = 0; i < statusList.Length; i++)
                {
                    var breakLoop = false;

                    while (statuses[currentIndex].replace)
                    {
                        ++currentIndex;
                        if (breakLoop = statuses.Count == currentIndex)
                            break;
                    }

                    if (breakLoop)
                        break;

                    var statusPtr = (Status*)statusList.GetStatusAddress(i);
                    if (IsStatusValid(statusPtr)) continue;

                    var status = statuses[currentIndex];
                    status.Apply(statusPtr, i);
                    ++currentIndex;
                    if (statuses.Count == currentIndex) break;
                }

                for (int i = 0; i < statuses.Count; i++)
                {
                    var status = statuses[i];
                    if (status.replace)
                        status.Replace(statusList);
                    else if (i >= currentIndex) // These statuses failed to find a free slot to apply
                        status.currentSlot = -1;
                }
            }

            public void CheckNewStatus(StatusList statusList, short slot, ushort statusID)
            {
                var reapply = false;
                for (int i = 0; i < statuses.Count; i++)
                {
                    var status = statuses[i];
                    var replaced = slot == status.currentSlot;
                    reapply = reapply || replaced;
                    if (status.replace || statusID != status.status.StatusID) continue;

                    if (!replaced)
                        status.TryRemove(statusList);

                    statuses.RemoveAt(i);
                    break;
                }

                if (reapply)
                    Apply(DalamudApi.ClientState.LocalPlayer!.StatusList);
            }

            public bool Remove(PredictedStatus status)
            {
                var removed = statuses.Remove(status);
                if (removed && DalamudApi.ClientState.LocalPlayer?.StatusList is { } statusList)
                    status.TryRemove(statusList);
                return removed;
            }

            public bool Contains(PredictedStatus status) => statuses.Contains(status);
        }

        private unsafe class PredictedStatus
        {
            public Status status = new();
            public float timer = 0;
            public bool replace = false;
            public short currentSlot = -1;

            public void Apply(Status* statusPtr, short slot)
            {
                statusPtr->StatusID = status.StatusID;
                statusPtr->StackCount = status.StackCount;
                statusPtr->Param = status.Param;
                currentSlot = slot;
            }

            public void Replace(StatusList statusList)
            {
                for (short i = 0; i < statusList.Length; i++)
                {
                    var statusPtr = (Status*)statusList.GetStatusAddress(i);
                    if (statusPtr->StatusID != status.StatusID) continue;
                    Apply(statusPtr, i);
                    return;
                }
            }

            public void TryRemove(StatusList statusList)
            {
                if (currentSlot < 0 || replace) return;
                var statusPtr = (Status*)statusList.GetStatusAddress(currentSlot);
                if (statusPtr->StatusID != status.StatusID || statusPtr->Param != status.Param || statusPtr->StackCount != status.StackCount) return;
                statusPtr->StatusID = 0;
                statusPtr->StackCount = 0;
                statusPtr->Param = 0;
                currentSlot = -1;
            }
        }

        private readonly PredictedStatusList predictedStatusList = new();
        private bool predictDualcast = false;
        private PredictedStatus dualCast = null;

        private class StatusInfo
        {
            public ushort id = 0;
            public byte stacks = 0;
            public byte param = 0;
            public float timer = 0.75f;
            public bool replace = false;
            public Action action = null;
        }

        private static unsafe void SwapMudras()
        {
            var jobGaugeManager = (IntPtr)JobGaugeManager.Instance();
            if (jobGaugeManager == IntPtr.Zero) return;
            *(byte*)(jobGaugeManager + 0x18) = 1;
        }

        private const ushort MudraStatusID = 496;
        private readonly Dictionary<uint, List<StatusInfo>> predictedStatuses = new()
        {
            [7421] = new() { new() { id = 1211, stacks = 3 } }, // Triplecast
            [7561] = new() { new() { id = 167 } }, // Swiftcast
            //[7383] = new() { new() { id = 1369 } }, // Requiescat
            //[23913] = new() { new() { id = 2560 } }, // Lost Chainspell
            // Firestarter?
            //[2259] = new() { new() { id = MudraStatusID, stacks = 1, timer = 1f } }, // Ten
            //[2261] = new() { new() { id = MudraStatusID, stacks = 2, timer = 1f } }, // Chi
            //[2263] = new() { new() { id = MudraStatusID, stacks = 3, timer = 1f } }, // Jin
            [2264] = new() { new() { id = 497, timer = 0.6f, action = SwapMudras } }, // Kassatsu
        };

        // Length - 7 seems to be the last one with sourceID 0xE0000000?
        private static unsafe bool IsStatusValid(Status* statusPtr) => statusPtr->StatusID != 0 && (statusPtr->RemainingTime > 0 || statusPtr->SourceID is not (0 or 0xE0000000));

        private void UseActionLocation(IntPtr actionManager, uint actionType, uint actionID, long targetedActorID, IntPtr vectorLocation, uint param)
        {
            if (actionType != 1) return;

            if (NoClippy.Config.PredictStatusApplications)
                PredictStatuses(actionID);

            if (NoClippy.Config.PredictMudras)
                PredictMudras(actionID);
        }

        private void PredictStatuses(uint actionID)
        {
            if (!predictedStatuses.TryGetValue(actionID, out var statuses)) return;

            foreach (var status in statuses)
            {
                predictedStatusList.Add(status.id, status.stacks, status.param, status.replace, status.timer);
                status.action?.Invoke();
            }

            predictedStatusList.Apply(DalamudApi.ClientState.LocalPlayer!.StatusList);
        }

        private void PredictMudras(uint actionID)
        {
            switch (actionID)
            {
                case 2259: // Ten
                case 18805:
                    UpdateMudraStatus(1);
                    break;
                case 2261: // Chi
                case 18806:
                    UpdateMudraStatus(2);
                    break;
                case 2263: // Jin
                case 18807:
                    UpdateMudraStatus(3);
                    break;
            }
        }

        private unsafe void UpdateMudraStatus(byte bit)
        {
            var mudraStacks = 0;

            var statusList = DalamudApi.ClientState.LocalPlayer!.StatusList;
            for (int i = 0; i < statusList.Length; i++)
            {
                var statusPtr = (Status*)statusList.GetStatusAddress(i);
                if (statusPtr->StatusID != MudraStatusID) continue;
                mudraStacks = statusPtr->StackCount;
                break;
            }

            switch (mudraStacks)
            {
                case > 63: // Already Failed
                    return;
                case > 15: // Fail
                    mudraStacks = 0xFF;
                    break;
                case > 3: // Third Mudra
                    mudraStacks += bit << 4;
                    break;
                case > 0: // Second Mudra
                    mudraStacks += bit << 2;
                    break;
                default: // First Mudra
                    predictedStatusList.Add(MudraStatusID, bit, 0, false, 1f);
                    predictedStatusList.Apply(statusList);
                    return;
            }

            if (mudraStacks > byte.MaxValue) return;

            predictedStatusList.Add(MudraStatusID, (byte)mudraStacks, 0, true, 1f);
            predictedStatusList.Apply(statusList);
            SwapMudras();
        }

        private void UpdateDualcast()
        {
            var statusList = DalamudApi.ClientState.LocalPlayer?.StatusList;
            if (statusList == null)
            {
                predictDualcast = false;
                return;
            }

            if (Game.IsCasting) return;
            dualCast = predictedStatusList.Add(1249);
            if (dualCast != null)
                predictedStatusList.Apply(statusList);
            predictDualcast = false;
        }

        private void CastBegin(ulong objectID, IntPtr packetData)
        {
            if (!NoClippy.Config.PredictDualcast || DalamudApi.ClientState.LocalPlayer?.ClassJob.Id != 35) return;
            dualCast = null;
            predictDualcast = true;
        }

        private void CastInterrupt(IntPtr actionManager, uint actionType, uint actionID)
        {
            if (!predictDualcast) return;
            predictedStatusList.Remove(dualCast);
            dualCast = null;
            predictDualcast = false;
        }

        private void Update()
        {
            predictedStatusList.Update((float)DalamudApi.Framework.UpdateDelta.TotalSeconds);

            if (predictDualcast)
                UpdateDualcast();
        }

        private void UpdateStatusList(StatusList statusList, short slot, ushort statusID, float remainingTime, ushort stackParam, uint sourceID)
        {
            if (slot < 0)
                predictedStatusList.Apply(statusList);
            else
                predictedStatusList.CheckNewStatus(statusList, slot, statusID);
        }

        private static void TextCenter(Vector4 color, string text)
        {
            ImGui.Spacing();
            var size = ImGui.CalcTextSize(text).X;
            ImGui.SetCursorPosX((ImGui.GetWindowSize().X - size) / 2);
            ImGui.TextColored(color, text);
        }

        public override void DrawConfig()
        {
            var red = new Vector4(1, 0.25f, 0.25f, 1);
            ImGui.BeginGroup();
            TextCenter(red, "!!!!!USE AT OWN RISK!!!!!");
            TextCenter(red, "Experimental prediction settings.");
            ImGui.Dummy(new Vector2(1000, 8));
            ImGui.EndGroup();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("This is a very early attempt at fixing a major problem with status effects and certain skills." +
                    "\nThe server should decline invalid attempts, but these settings could cause more invalid packets than usual." +
                    "\nIt is currently not known if these settings can produce unexpected results on FFLogs.");

            ImGui.Columns(2, null, false);

            if (ImGui.Checkbox("Predict Statuses", ref NoClippy.Config.PredictStatusApplications))
                NoClippy.Config.Save();
            PluginUI.SetItemTooltip("Removes the effects of lag on certain statuses." +
                "\nCurrently supported:\nSwiftcast\nTriplecast\nKassatsu");

            ImGui.NextColumn();

            if (ImGui.Checkbox("Predict Mudras", ref NoClippy.Config.PredictMudras))
                NoClippy.Config.Save();
            PluginUI.SetItemTooltip("Removes the effects of lag on using Mudras and Ninjutsu.");

            ImGui.NextColumn();

            if (ImGui.Checkbox("Predict Dualcast", ref NoClippy.Config.PredictDualcast))
                NoClippy.Config.Save();
            PluginUI.SetItemTooltip("Mostly removes the effects of lag on dualcast." +
                "\nWarning: Can easily desync with extremely high lag and slidecasting too early.");

            ImGui.Columns(1);
        }

        public override void Enable()
        {
            Game.OnUseActionLocation += UseActionLocation;
            Game.OnUpdate += Update;
            Game.OnUpdateStatusList += UpdateStatusList;
            Game.OnCastBegin += CastBegin;
            Game.OnCastInterrupt += CastInterrupt;
        }

        public override void Disable()
        {
            Game.OnUseActionLocation -= UseActionLocation;
            Game.OnUpdate -= Update;
            Game.OnUpdateStatusList -= UpdateStatusList;
            Game.OnCastBegin -= CastBegin;
            Game.OnCastInterrupt -= CastInterrupt;
        }
    }
}
