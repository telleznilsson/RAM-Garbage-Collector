using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Server;

namespace GenericStructureRAMFix.Systems
{
    public class AggressiveItemMergeSystem : ModSystem
    {
        private ICoreServerAPI sapi;

        public override bool ShouldLoad(EnumAppSide forSide)
        {
            return forSide == EnumAppSide.Server;
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            api.Event.OnEntitySpawn += OnEntitySpawn;
        }

        private void OnEntitySpawn(Entity entity)
        {
            if (entity is not EntityItem newEntityItem || newEntityItem.Itemstack == null)
            {
                return;
            }

            Entity[] nearbyEntities = sapi.World.GetEntitiesAround(
                newEntityItem.Pos.XYZ,
                2.5f,
                2.5f,
                (e) => e is EntityItem && e.EntityId != newEntityItem.EntityId
            );

            foreach (Entity nearby in nearbyEntities)
            {
                if (nearby is not EntityItem existingItem || !existingItem.Alive || existingItem.Itemstack == null)
                {
                    continue;
                }

                if (!existingItem.Itemstack.Equals(sapi.World, newEntityItem.Itemstack))
                {
                    continue;
                }

                int spaceLeft = existingItem.Itemstack.Collectible.MaxStackSize - existingItem.Itemstack.StackSize;
                if (spaceLeft <= 0)
                {
                    continue;
                }

                if (spaceLeft >= newEntityItem.Itemstack.StackSize)
                {
                    existingItem.Itemstack.StackSize += newEntityItem.Itemstack.StackSize;
                    newEntityItem.Itemstack.StackSize = 0;
                    existingItem.WatchedAttributes.MarkPathDirty("itemstack");
                    sapi.World.DespawnEntity(newEntityItem, new EntityDespawnData
                    {
                        Reason = EnumDespawnReason.Removed
                    });
                    break;
                }

                existingItem.Itemstack.StackSize += spaceLeft;
                newEntityItem.Itemstack.StackSize -= spaceLeft;
                existingItem.WatchedAttributes.MarkPathDirty("itemstack");
                newEntityItem.WatchedAttributes.MarkPathDirty("itemstack");
            }
        }
    }
}