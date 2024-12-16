using jellyfin_ani_sync.Configuration;
using System;
using System.Collections.Generic;
using System.Linq;

namespace jellyfin_ani_sync.Models
{
    public class UserConfig
    {
        public UserConfig()
        {
            // set default options here
            PlanToWatchOnly = true;
            RewatchCompleted = true;
        }

        /// <summary>
        /// ID of the user.
        /// </summary>
        public Guid UserId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the API should only search for shows on the users plan to watch list.
        /// </summary>
        public bool PlanToWatchOnly { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the plugin should automatically set completed shows as re-watching.
        /// </summary>
        public bool RewatchCompleted { get; set; }

        /// <summary>
        /// API authentication details of the user.
        /// </summary>
        public UserApiAuth[] UserApiAuth { get; set; }

        /// <summary>
        /// Key pair values for any data that needs to be stored but doesn't fit anywhere else.
        /// </summary>
        public List<KeyPairs> KeyPairs { get; set; }

        public void AddUserApiAuth(UserApiAuth userApiAuth)
        {
            if (UserApiAuth != null)
            {
                var apiAuthList = UserApiAuth.ToList();
                apiAuthList.Add(userApiAuth);
                UserApiAuth = apiAuthList.ToArray();
            }
            else
            {
                UserApiAuth = new[] { userApiAuth };
            }
        }

        public string[] LibraryToCheck { get; set; }
    }
}