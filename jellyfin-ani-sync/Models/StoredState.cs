using jellyfin_ani_sync.Configuration;
using System;

namespace jellyfin_ani_sync.Models;

public class StoredState
{
    public Guid UserId { get; set; }
    public ApiName ApiName { get; set; }
}