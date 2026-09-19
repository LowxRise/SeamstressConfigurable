using System.Linq;
using RoR2;
using UnityEngine;
using UnityEngine.Networking;

namespace SeamstressConfigurable
{
    internal sealed class SkewerTargetTracker : MonoBehaviour
    {
        internal static GameObject IndicatorPrefab;

        private readonly BullseyeSearch search = new BullseyeSearch();
        private CharacterBody body;
        private InputBankTest inputBank;
        private NetworkIdentity networkIdentity;
        private TeamComponent team;
        private Indicator indicator;
        private HurtBox target;
        private float stopwatch;

        internal HurtBox Target => target && target.healthComponent && target.healthComponent.alive ? target : null;

        private void Awake()
        {
            body = GetComponent<CharacterBody>();
            inputBank = GetComponent<InputBankTest>();
            networkIdentity = GetComponent<NetworkIdentity>();
            team = GetComponent<TeamComponent>();
            if (IndicatorPrefab)
                indicator = new Indicator(gameObject, IndicatorPrefab);
        }

        private void OnDisable() => ResetTarget();

        private void FixedUpdate()
        {
            if (!CanTrack())
            {
                ResetTarget();
                return;
            }

            stopwatch += Time.fixedDeltaTime;
            if (stopwatch >= 0.1f)
            {
                stopwatch -= 0.1f;
                FindTarget();
            }

            if (indicator != null)
            {
                indicator.active = networkIdentity && Util.HasEffectiveAuthority(networkIdentity);
                indicator.targetTransform = Target ? Target.transform : null;
            }
        }

        private bool CanTrack()
        {
            var skill = body && body.skillLocator ? body.skillLocator.special : null;
            return isActiveAndEnabled && inputBank && team && skill && skill.skillDef &&
                skill.skillDef.skillNameToken == "KENKO_SEAMSTRESS_SPECIAL_FIRE_NAME";
        }

        private void FindTarget()
        {
            search.teamMaskFilter = TeamMask.GetUnprotectedTeams(team.teamIndex);
            search.filterByLoS = true;
            search.searchOrigin = inputBank.aimOrigin;
            search.searchDirection = inputBank.aimDirection;
            search.sortMode = BullseyeSearch.SortMode.Angle;
            search.maxDistanceFilter = 60f;
            search.maxAngleFilter = 30f;
            search.RefreshCandidates();
            search.FilterOutGameObject(gameObject);
            target = search.GetResults().FirstOrDefault(hit => hit.healthComponent && hit.healthComponent.alive);
        }

        private void ResetTarget()
        {
            target = null;
            stopwatch = 0f;
            if (indicator == null)
                return;
            indicator.active = false;
            indicator.targetTransform = null;
        }
    }
}
