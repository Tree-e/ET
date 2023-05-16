using System.IO;
using ET.Client;

namespace ET
{
    public static class LSHelper
    {
        public static void RunRollbackSystem(Entity entity)
        {
            if (entity is LSEntity)
            {
                return;
            }
            
            LSEntitySystemSington.Instance.Rollback(entity);
            
            if (entity.ComponentsCount() > 0)
            {
                foreach (var kv in entity.Components)
                {
                    RunRollbackSystem(kv.Value);
                }
            }

            if (entity.ChildrenCount() > 0)
            {
                foreach (var kv in entity.Children)
                {
                    RunRollbackSystem(kv.Value);
                }
            }
        }
        
        // 回滚
        public static void Rollback(Room room, int frame)
        {
            Log.Debug($"roll back start {frame}");
            room.LSWorld.Dispose();
            FrameBuffer frameBuffer = room.FrameBuffer;
            
            // 回滚
            room.LSWorld = room.GetLSWorld(SceneType.LockStepClient, frame);
            OneFrameInputs authorityFrameInput = frameBuffer.FrameInputs(frame);
            // 执行AuthorityFrame
            room.Update(authorityFrameInput);
            room.SendHash(frame);

            
            // 重新执行预测的帧
            for (int i = room.AuthorityFrame + 1; i <= room.PredictionFrame; ++i)
            {
                OneFrameInputs oneFrameInputs = frameBuffer.FrameInputs(i);
                LSHelper.CopyOtherInputsTo(room, authorityFrameInput, oneFrameInputs); // 重新预测消息
                room.Update(oneFrameInputs);
            }
            
            RunRollbackSystem(room);
            
            Log.Debug($"roll back finish {frame}");
        }
        
        public static void SendHash(this Room self, int frame)
        {
            if (frame > self.AuthorityFrame)
            {
                return;
            }
            long hash = self.FrameBuffer.GetHash(frame);
            C2Room_CheckHash c2RoomCheckHash = NetServices.Instance.FetchMessage<C2Room_CheckHash>();
            c2RoomCheckHash.Frame = frame;
            c2RoomCheckHash.Hash = hash;
            self.GetParent<Scene>().GetComponent<SessionComponent>().Session.Send(c2RoomCheckHash);
        }
        
        // 重新调整预测消息，只需要调整其他玩家的输入
        public static void CopyOtherInputsTo(Room room, OneFrameInputs from, OneFrameInputs to)
        {
            long myId = room.GetComponent<LSClientUpdater>().MyId;
            foreach (var kv in from.Inputs)
            {
                if (kv.Key == myId)
                {
                    continue;
                }
                to.Inputs[kv.Key] = kv.Value;
            }
        }

        public static void SaveReplay(Room room, string path)
        {
            if (room.IsReplay)
            {
                return;
            }
            Log.Debug($"save replay: {path} frame: {room.Replay.FrameInputs.Count}");
            byte[] bytes = MemoryPackHelper.Serialize(room.Replay);
            File.WriteAllBytes(path, bytes);
        }
        
        public static void JumpReplay(Room room, int frame)
        {
            if (!room.IsReplay)
            {
                return;
            }

            if (frame >= room.Replay.FrameInputs.Count)
            {
                frame = room.Replay.FrameInputs.Count - 1;
            }
            
            int snapshotIndex = frame / LSConstValue.SaveLSWorldFrameCount;
            Log.Debug($"jump replay start {room.AuthorityFrame} {frame} {snapshotIndex}");
            if (snapshotIndex != room.AuthorityFrame / LSConstValue.SaveLSWorldFrameCount || frame < room.AuthorityFrame)
            {
                room.LSWorld.Dispose();
                // 回滚
                byte[] memoryBuffer = room.Replay.Snapshots[snapshotIndex];
                LSWorld lsWorld = MongoHelper.Deserialize(typeof (LSWorld), memoryBuffer, 0, memoryBuffer.Length) as LSWorld;
                room.LSWorld = lsWorld;
                room.AuthorityFrame = snapshotIndex * LSConstValue.SaveLSWorldFrameCount;
                RunRollbackSystem(room);
            }
            
            room.FixedTimeCounter.Reset(TimeHelper.ServerFrameTime() - frame * LSConstValue.UpdateInterval, 0);

            Log.Debug($"jump replay finish {frame}");
        }
    }
}