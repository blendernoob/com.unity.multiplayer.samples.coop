using System;
using System.Numerics;
using JetBrains.Annotations;
using Unity.BossRoom.Gameplay.Actions;
using Unity.BossRoom.Gameplay.Configuration;
using Unity.BossRoom.Gameplay.GameplayObjects;
using Unity.BossRoom.Gameplay.GameplayObjects.Character;
using Unity.BossRoom.Gameplay.UI;
using Unity.Multiplayer.Samples.BossRoom;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Assertions;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

using Vector2 = UnityEngine.Vector2;
using Vector3 = UnityEngine.Vector3;

namespace Unity.BossRoom.Gameplay.UserInput
{
    /// <summary>
    /// Captures inputs for a character on a client and sends them to the server.
    /// </summary>
    [RequireComponent(typeof(ServerCharacter))]
    public class ClientInputSender : NetworkBehaviour
    {
        const float k_MouseInputRaycastDistance = 100f;

        //The movement input rate is capped at 40ms (or 25 fps). This provides a nice balance between responsiveness and
        //upstream network conservation. This matters when holding down your mouse button to move.
        const float k_MoveSendRateSeconds = 0.04f; //25 fps.

        const float k_TargetMoveTimeout = 0.45f;  //prevent moves for this long after targeting someone (helps prevent walking to the guy you clicked).

        float m_LastSentMove;

        public PlayerNewInput newInput;

        // Cache raycast hit array so that we can use non alloc raycasts
        readonly RaycastHit[] k_CachedHit = new RaycastHit[4];

        // This is basically a constant but layer masks cannot be created in the constructor, that's why it's assigned int Awake.
        LayerMask m_GroundLayerMask;

        LayerMask m_ActionLayerMask;

        const float k_MaxNavMeshDistance = 1f;

        RaycastHitComparer m_RaycastHitComparer;

        [SerializeField]
        ServerCharacter m_ServerCharacter;

        /// <summary>
        /// This event fires at the time when an action request is sent to the server.
        /// </summary>
        public event Action<ActionRequestData> ActionInputEvent;

        /// <summary>
        /// This describes how a skill was requested. Skills requested via mouse click will do raycasts to determine their target; skills requested
        /// in other matters will use the stateful target stored in NetworkCharacterState.
        /// </summary>
        public enum SkillTriggerStyle
        {
            None,        //no skill was triggered.
            MouseClick,  //skill was triggered via mouse-click implying you should do a raycast from the mouse position to find a target.
            Keyboard,    //skill was triggered via a Keyboard press, implying target should be taken from the active target.
            KeyboardRelease, //represents a released key.
            UI,          //skill was triggered from the UI, and similar to Keyboard, target should be inferred from the active target.
            UIRelease,   //represents letting go of the mouse-button on a UI button
        }

        bool IsReleaseStyle(SkillTriggerStyle style)
        {
            return style == SkillTriggerStyle.KeyboardRelease || style == SkillTriggerStyle.UIRelease;
        }

        /// <summary>
        /// This struct essentially relays the call params of RequestAction to FixedUpdate. Recall that we may need to do raycasts
        /// as part of doing the action, and raycasts done outside of FixedUpdate can give inconsistent results (otherwise we would
        /// just expose PerformAction as a public method, and let it be called in whatever scoped it liked.
        /// </summary>
        /// <remarks>
        /// Reference: https://answers.unity.com/questions/1141633/why-does-fixedupdate-work-when-update-doesnt.html
        /// </remarks>
        struct ActionRequest
        {
            public SkillTriggerStyle TriggerStyle;
            public ActionID RequestedActionID;
            public ulong TargetId;
        }

        /// <summary>
        /// List of ActionRequests that have been received since the last FixedUpdate ran. This is a static array, to avoid allocs, and
        /// because we don't really want to let this list grow indefinitely.
        /// </summary>
        readonly ActionRequest[] m_ActionRequests = new ActionRequest[5];

        /// <summary>
        /// Number of ActionRequests that have been queued since the last FixedUpdate.
        /// </summary>
        int m_ActionRequestCount;

        BaseActionInput m_CurrentSkillInput;

        bool m_MoveRequest;
        bool m_ControllerMovement;
        bool m_RotateRequest;

        Camera m_MainCamera;

        public event Action<Vector3> ClientMoveEvent;

        private UISettingsCanvas uiManager;

        /// <summary>
        /// Convenience getter that returns our CharacterData
        /// </summary>
        CharacterClass CharacterClass => m_ServerCharacter.CharacterClass;

        [SerializeField]
        PhysicsWrapper m_PhysicsWrapper;

        public ActionState actionState1 { get; private set; }

        public ActionState actionState2 { get; private set; }

        public ActionState actionState3 { get; private set; }

        public System.Action action1ModifiedCallback;

        ServerCharacter m_TargetServerCharacter;

        NewInputSystem inputSystem;

        Vector2 mousePosition;
        Vector3 newMousePosition;

        Vector2 movementVecotr;
        [SerializeField] float movementSensitivity; 

        [SerializeField] Transform controllerRayObject;
        [SerializeField] Light controllerRayObjectLight;
        void Awake()
        {
            m_MainCamera = Camera.main;
            newInput = GetComponent<PlayerNewInput>();
            
            inputSystem = new NewInputSystem();

            //inputSystem.Game.Rotate.performed += InputRotateCharacter; 
            inputSystem.Game.Aim.performed += OnAim;

            controllerRayObject = GameObject.Find("ControllerRayObject").GetComponent<Transform>();
            controllerRayObjectLight = GameObject.Find("TestPointLight").GetComponent<Light>();

            uiManager = GameObject.Find("SettingsPanelCanvas").GetComponent<UISettingsCanvas>();
        }

        public override void OnNetworkSpawn()
        {
            if (!IsClient || !IsOwner)
            {
                enabled = false;
                // dont need to do anything else if not the owner
                return;
            }

            m_ServerCharacter.TargetId.OnValueChanged += OnTargetChanged;
            m_ServerCharacter.HeldNetworkObject.OnValueChanged += OnHeldNetworkObjectChanged;

            if (CharacterClass.Skill1 &&
                GameDataSource.Instance.TryGetActionPrototypeByID(CharacterClass.Skill1.ActionID, out var action1))
            {
                actionState1 = new ActionState() { actionID = action1.ActionID, selectable = true };
            }
            if (CharacterClass.Skill2 &&
                GameDataSource.Instance.TryGetActionPrototypeByID(CharacterClass.Skill2.ActionID, out var action2))
            {
                actionState2 = new ActionState() { actionID = action2.ActionID, selectable = true };
            }
            if (CharacterClass.Skill3 &&
                GameDataSource.Instance.TryGetActionPrototypeByID(CharacterClass.Skill3.ActionID, out var action3))
            {
                actionState3 = new ActionState() { actionID = action3.ActionID, selectable = true };
            }

            m_GroundLayerMask = LayerMask.GetMask(new[] { "Ground" });
            m_ActionLayerMask = LayerMask.GetMask(new[] { "PCs", "NPCs", "Ground" });

            m_RaycastHitComparer = new RaycastHitComparer();
        }

        public override void OnNetworkDespawn()
        {
            if (m_ServerCharacter)
            {
                m_ServerCharacter.TargetId.OnValueChanged -= OnTargetChanged;
                m_ServerCharacter.HeldNetworkObject.OnValueChanged -= OnHeldNetworkObjectChanged;
            }

            if (m_TargetServerCharacter)
            {
                m_TargetServerCharacter.NetLifeState.LifeState.OnValueChanged -= OnTargetLifeStateChanged;
            }
        }

        void OnTargetChanged(ulong previousValue, ulong newValue)
        {
            if (m_TargetServerCharacter)
            {
                m_TargetServerCharacter.NetLifeState.LifeState.OnValueChanged -= OnTargetLifeStateChanged;
            }

            m_TargetServerCharacter = null;

            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(newValue, out var selection) &&
                selection.TryGetComponent(out m_TargetServerCharacter))
            {
                m_TargetServerCharacter.NetLifeState.LifeState.OnValueChanged += OnTargetLifeStateChanged;
            }

            UpdateAction1();
        }

        void OnHeldNetworkObjectChanged(ulong previousValue, ulong newValue)
        {
            UpdateAction1();
        }

        void OnTargetLifeStateChanged(LifeState previousValue, LifeState newValue)
        {
            UpdateAction1();
        }

        void FinishSkill()
        {
            m_CurrentSkillInput = null;
        }

        void SendInput(ActionRequestData action)
        {
            ActionInputEvent?.Invoke(action);
            m_ServerCharacter.ServerPlayActionRpc(action);
        }

        void FixedUpdate()
        {
            //Debug.Log(m_ControllerMovement);

            Vector2 inputVector = inputSystem.Game.Rotate.ReadValue<Vector2>();

            //play all ActionRequests, in FIFO order.
            for (int i = 0; i < m_ActionRequestCount; ++i)
            {
                if (m_CurrentSkillInput != null)
                {
                    //actions requested while input is active are discarded, except for "Release" requests, which go through.
                    if (IsReleaseStyle(m_ActionRequests[i].TriggerStyle))
                    {
                        m_CurrentSkillInput.OnReleaseKey();
                    }
                }
                else if (!IsReleaseStyle(m_ActionRequests[i].TriggerStyle))
                {
                    var actionPrototype = GameDataSource.Instance.GetActionPrototypeByID(m_ActionRequests[i].RequestedActionID);
                    if (actionPrototype.Config.ActionInput != null)
                    {
                        var skillPlayer = Instantiate(actionPrototype.Config.ActionInput);
                        skillPlayer.Initiate(m_ServerCharacter, m_PhysicsWrapper.Transform.position, actionPrototype.ActionID, SendInput, FinishSkill);
                        m_CurrentSkillInput = skillPlayer;
                    }
                    else
                    {
                        PerformSkill(actionPrototype.ActionID, m_ActionRequests[i].TriggerStyle, m_ActionRequests[i].TargetId);
                    }
                }
            }

            m_ActionRequestCount = 0;

            if (EventSystem.current.currentSelectedGameObject != null)
            {
                return;
            }

            //we are calling this function every frame and checking if the moverequest is true
            //this is done so we can try to invoke it as a unity event
            
            // we can potetntailly refactor all of this into a function
            //moving the character to a certain position
            if (m_MoveRequest)
            {
                //for when we use the mouse
                if (m_ControllerMovement == false)
                {
                    Debug.Log("The request has been sent");
                    m_MoveRequest = false;

                    controllerRayObjectLight.intensity = 0;

                    if ((Time.time - m_LastSentMove) > k_MoveSendRateSeconds)
                    {
                        Debug.Log("The REquest has been processed");
                        m_LastSentMove = Time.time;

                        //find an input system equivalent to mouse position
                        var ray = m_MainCamera.ScreenPointToRay(newMousePosition);

                        var groundHits = Physics.RaycastNonAlloc(ray, k_CachedHit, k_MouseInputRaycastDistance, m_GroundLayerMask);

                        if (groundHits > 0)
                        {
                            if (groundHits > 1)
                            {
                                // sort hits by distance
                                Array.Sort(k_CachedHit, 0, groundHits, m_RaycastHitComparer);
                            }

                            // verify point is indeed on navmesh surface
                            if (NavMesh.SamplePosition(k_CachedHit[0].point, out var hit,
                                k_MaxNavMeshDistance,
                                NavMesh.AllAreas))
                            {
                                m_ServerCharacter.ServerSendCharacterInputRpc(hit.position);

                                //Send our client only click request
                                //take a look at this as well
                                ClientMoveEvent?.Invoke(hit.position);
                            }
                        }
                    }

                    //after all of that, we can now set the controller movement to false
                    m_ControllerMovement = false;
                }
                else if (m_ControllerMovement == true)
                {
                    //Debug.Log("The request has been sent but for a controller this time");
                    m_MoveRequest = false;
                    
                    if ((Time.time - m_LastSentMove) > k_MoveSendRateSeconds)
                    {
                         //remember transform.forward is the z axis
                        var ray = new Ray(controllerRayObject.transform.position, controllerRayObject.transform.up * -1);

                        var groundHits = Physics.RaycastNonAlloc(ray, k_CachedHit, k_MouseInputRaycastDistance, m_GroundLayerMask);

                        Vector3 directionOfRay = ray.direction;

                        Debug.DrawRay(ray.origin, directionOfRay);

                        if(groundHits > 1)
                        {
                            Array.Sort(k_CachedHit, 0, groundHits, m_RaycastHitComparer);
                        }

                        if(NavMesh.SamplePosition(k_CachedHit[0].point, out var hit, k_MaxNavMeshDistance, NavMesh.AllAreas))
                        {
                            m_ServerCharacter.ServerSendCharacterInputRpc(hit.position);

                            ClientMoveEvent?.Invoke(hit.position);
                            
                        }
                    }
                    m_ControllerMovement = false;
                }
            }
        }

        public void OnAim(InputAction.CallbackContext context)
        {
            mousePosition = context.ReadValue<Vector2>();
            newMousePosition = mousePosition;

            //Debug.Log(newMousePosition);
        }

        /// <summary>
        /// Perform a skill in response to some input trigger. This is the common method to which all input-driven skill plays funnel.
        /// </summary>
        /// <param name="actionID">The action you want to play. Note that "Skill1" may be overriden contextually depending on the target.</param>
        /// <param name="triggerStyle">What sort of input triggered this skill?</param>
        /// <param name="targetId">(optional) Pass in a specific networkID to target for this action</param>
        void PerformSkill(ActionID actionID, SkillTriggerStyle triggerStyle, ulong targetId = 0)
        {
            Transform hitTransform = null;

            if (targetId != 0)
            {
                // if a targetId is given, try to find the object
                NetworkObject targetNetObj;
                if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetId, out targetNetObj))
                {
                    hitTransform = targetNetObj.transform;
                }
            }
            else
            {
                // otherwise try to find an object under the input position
                int numHits = 0;
                if (triggerStyle == SkillTriggerStyle.MouseClick)
                {
                    var ray = m_MainCamera.ScreenPointToRay(newMousePosition);
                    numHits = Physics.RaycastNonAlloc(ray, k_CachedHit, k_MouseInputRaycastDistance, m_ActionLayerMask);
                }

                int networkedHitIndex = -1;
                for (int i = 0; i < numHits; i++)
                {
                    if (k_CachedHit[i].transform.GetComponentInParent<NetworkObject>())
                    {
                        networkedHitIndex = i;
                        break;
                    }
                }

                hitTransform = networkedHitIndex >= 0 ? k_CachedHit[networkedHitIndex].transform : null;
            }

            if (GetActionRequestForTarget(hitTransform, actionID, triggerStyle, out ActionRequestData playerAction))
            {
                //Don't trigger our move logic for a while. This protects us from moving just because we clicked on them to target them.
                m_LastSentMove = Time.time + k_TargetMoveTimeout;

                SendInput(playerAction);
            }
            else if (!GameDataSource.Instance.GetActionPrototypeByID(actionID).IsGeneralTargetAction)
            {
                // clicked on nothing... perform an "untargeted" attack on the spot they clicked on.
                // (Different Actions will deal with this differently. For some, like archer arrows, this will fire an arrow
                // in the desired direction. For others, like mage's bolts, this will fire a "miss" projectile at the spot clicked on.)

                var data = new ActionRequestData();
                PopulateSkillRequest(k_CachedHit[0].point, actionID, ref data);

                SendInput(data);
            }
        }

        /// <summary>
        /// When you right-click on something you will want to do contextually different things. For example you might attack an enemy,
        /// but revive a friend. You might also decide to do nothing (e.g. right-clicking on a friend who hasn't FAINTED).
        /// </summary>
        /// <param name="hit">The Transform of the entity we clicked on, or null if none.</param>
        /// <param name="actionID">The Action to build for</param>
        /// <param name="triggerStyle">How did this skill play get triggered? Mouse, Keyboard, UI etc.</param>
        /// <param name="resultData">Out parameter that will be filled with the resulting action, if any.</param>
        /// <returns>true if we should play an action, false otherwise. </returns>
        bool GetActionRequestForTarget(Transform hit, ActionID actionID, SkillTriggerStyle triggerStyle, out ActionRequestData resultData)
        {
            resultData = new ActionRequestData();

            var targetNetObj = hit != null ? hit.GetComponentInParent<NetworkObject>() : null;

            //if we can't get our target from the submitted hit transform, get it from our stateful target in our ServerCharacter.
            if (!targetNetObj && !GameDataSource.Instance.GetActionPrototypeByID(actionID).IsGeneralTargetAction)
            {
                ulong targetId = m_ServerCharacter.TargetId.Value;
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetId, out targetNetObj);
            }

            //sanity check that this is indeed a valid target.
            if (targetNetObj == null || !ActionUtils.IsValidTarget(targetNetObj.NetworkObjectId))
            {
                return false;
            }

            if (targetNetObj.TryGetComponent<ServerCharacter>(out var serverCharacter))
            {
                //Skill1 may be contextually overridden if it was generated from a mouse-click.
                if (actionID == CharacterClass.Skill1.ActionID && triggerStyle == SkillTriggerStyle.MouseClick)
                {
                    if (!serverCharacter.IsNpc && serverCharacter.LifeState == LifeState.Fainted)
                    {
                        //right-clicked on a downed ally--change the skill play to Revive.
                        actionID = GameDataSource.Instance.ReviveActionPrototype.ActionID;
                    }
                }
            }

            Vector3 targetHitPoint;
            if (PhysicsWrapper.TryGetPhysicsWrapper(targetNetObj.NetworkObjectId, out var movementContainer))
            {
                targetHitPoint = movementContainer.Transform.position;
            }
            else
            {
                targetHitPoint = targetNetObj.transform.position;
            }

            // record our target in case this action uses that info (non-targeted attacks will ignore this)
            resultData.ActionID = actionID;
            resultData.TargetIds = new ulong[] { targetNetObj.NetworkObjectId };
            PopulateSkillRequest(targetHitPoint, actionID, ref resultData);
            return true;
        }

        /// <summary>
        /// Populates the ActionRequestData with additional information. The TargetIds of the action should already be set before calling this.
        /// </summary>
        /// <param name="hitPoint">The point in world space where the click ray hit the target.</param>
        /// <param name="actionID">The action to perform (will be stamped on the resultData)</param>
        /// <param name="resultData">The ActionRequestData to be filled out with additional information.</param>
        void PopulateSkillRequest(Vector3 hitPoint, ActionID actionID, ref ActionRequestData resultData)
        {
            resultData.ActionID = actionID;
            var actionConfig = GameDataSource.Instance.GetActionPrototypeByID(actionID).Config;

            //most skill types should implicitly close distance. The ones that don't are explicitly set to false in the following switch.
            resultData.ShouldClose = true;

            // figure out the Direction in case we want to send it
            Vector3 offset = hitPoint - m_PhysicsWrapper.Transform.position;
            offset.y = 0;
            Vector3 direction = offset.normalized;

            switch (actionConfig.Logic)
            {
                //for projectile logic, infer the direction from the click position.
                case ActionLogic.LaunchProjectile:
                    resultData.Direction = direction;
                    resultData.ShouldClose = false; //why? Because you could be lining up a shot, hoping to hit other people between you and your target. Moving you would be quite invasive.
                    return;
                case ActionLogic.Melee:
                    resultData.Direction = direction;
                    return;
                case ActionLogic.Target:
                    resultData.ShouldClose = false;
                    return;
                case ActionLogic.Emote:
                    resultData.CancelMovement = true;
                    return;
                case ActionLogic.RangedFXTargeted:
                    resultData.Position = hitPoint;
                    return;
                case ActionLogic.DashAttack:
                    resultData.Position = hitPoint;
                    return;
                case ActionLogic.PickUp:
                    resultData.CancelMovement = true;
                    resultData.ShouldQueue = false;
                    return;
            }
        }

        /// <summary>
        /// Request an action be performed. This will occur on the next FixedUpdate.
        /// </summary>
        /// <param name="actionID"> The action you'd like to perform. </param>
        /// <param name="triggerStyle"> What input style triggered this action. </param>
        /// <param name="targetId"> NetworkObjectId of target. </param>
        public void RequestAction(ActionID actionID, SkillTriggerStyle triggerStyle, ulong targetId = 0)
        {
            Assert.IsNotNull(GameDataSource.Instance.GetActionPrototypeByID(actionID),
                $"Action with actionID {actionID} must be contained in the Action prototypes of GameDataSource!");

            if (m_ActionRequestCount < m_ActionRequests.Length)
            {
                m_ActionRequests[m_ActionRequestCount].RequestedActionID = actionID;
                m_ActionRequests[m_ActionRequestCount].TriggerStyle = triggerStyle;
                m_ActionRequests[m_ActionRequestCount].TargetId = targetId;
                m_ActionRequestCount++;
            }
        }

// here is our input. We need to convert this to the new input system
//if this implementation doesnt work, will will refactor all of these as functions for the input system to take
        void Update()
        {
            //Debug.Log(EventSystem.current);

            #region The old input system
            //1 key
            //if (Input.GetKeyDown(KeyCode.Alpha1) && CharacterClass.Skill1)
            // if(newInput.skill1 == true && CharacterClass.Skill1)
            // {
            //     RequestAction(actionState1.actionID, SkillTriggerStyle.Keyboard);
            // }
            // //else if (Input.GetKeyUp(KeyCode.Alpha1) && CharacterClass.Skill1)
            // else if (newInput.skill1 == false && CharacterClass.Skill1)
            // {
            //     RequestAction(actionState1.actionID, SkillTriggerStyle.KeyboardRelease);
            // }

            // //2 key
            // if (Input.GetKeyDown(KeyCode.Alpha2) && CharacterClass.Skill2)
            // {
            //     RequestAction(actionState2.actionID, SkillTriggerStyle.Keyboard);
            // }
            // else if (Input.GetKeyUp(KeyCode.Alpha2) && CharacterClass.Skill2)
            // {
            //     RequestAction(actionState2.actionID, SkillTriggerStyle.KeyboardRelease);
            // }

            // //3 key
            // if (Input.GetKeyDown(KeyCode.Alpha3) && CharacterClass.Skill3)
            // {
            //     RequestAction(actionState3.actionID, SkillTriggerStyle.Keyboard);
            // }
            // else if (Input.GetKeyUp(KeyCode.Alpha3) && CharacterClass.Skill3)
            // {
            //     RequestAction(actionState3.actionID, SkillTriggerStyle.KeyboardRelease);
            // }

            //5 key
            // if (Input.GetKeyDown(KeyCode.Alpha5))
            // {
            //     RequestAction(GameDataSource.Instance.Emote1ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            // }

            // //6 key
            // if (Input.GetKeyDown(KeyCode.Alpha6))
            // {
            //     RequestAction(GameDataSource.Instance.Emote2ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            // }

            // //7 key
            // if (Input.GetKeyDown(KeyCode.Alpha7))
            // {
            //     RequestAction(GameDataSource.Instance.Emote3ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            // }

            // //8 key
            // if (Input.GetKeyDown(KeyCode.Alpha8))
            // {
            //     RequestAction(GameDataSource.Instance.Emote4ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            // }
            #endregion
                        
            //the original way of doing movement with the original input system

            //we are keeping this in here for mouse movement only
            //controllers will work in the input system

            //if the mouse is over a UI element and no skill is being used?

            if(m_CurrentSkillInput == null)
            {
                if(!EventSystem.current.IsPointerOverGameObject())
                {
                    //IsPointerOverGameObject() is a simple way to determine if the mouse is over a UI element. If it is, we don't perform mouse input logic,
                    //to model the button "blocking" mouse clicks from falling through and interacting with the world.

                    //right mouse button
                    if (Input.GetMouseButtonDown(1))
                    {
                        RequestAction(CharacterClass.Skill1.ActionID, SkillTriggerStyle.MouseClick);
                    }

                    //left mouse button
                    if (Input.GetMouseButtonDown(0))
                    //if(newInput.move == true || Input.GetMouseButtonDown(0))
                    {
                        RequestAction(GameDataSource.Instance.GeneralTargetActionPrototype.ActionID, SkillTriggerStyle.MouseClick);
                        Debug.Log("You should be moving right now");
                    }
                    else if (Input.GetMouseButton(0))
                    //else if(newInput.move == true || Input.GetMouseButtonDown(0))
                    {
                        m_MoveRequest = true;
                    }
                }
            }   
        }

#region New input System Stuff

        //my implementation of the movement system using the new input system
        //this is interacting with the actual player input component
        // //need to find out why the else if statement doesnt function exactly like the way in the update section that is commented
        // public void InputMove(InputAction.CallbackContext context)
        // {
        //     if(context.control.device is Keyboard or Mouse)
        //     {
        //         if(!EventSystem.current.IsPointerOverGameObject() && m_CurrentSkillInput == null)
        //         {
        //             if(inputSystem.Game.Move.WasPressedThisFrame())
        //             {
        //                 Debug.Log("Now how do I implement this into the game propper?");
        //                 RequestAction(GameDataSource.Instance.GeneralTargetActionPrototype.ActionID, SkillTriggerStyle.MouseClick);
        //             }
        //             else if(inputSystem.Game.Move.WasPressedThisFrame())
        //             {
        //                 Debug.Log("How about now");
        //                 m_MoveRequest = true;
        //             }
        //         }
        //     }
        // }

        //this is the first part of the if statement. so I actually should add the if 
        // public void InputAttack(InputAction.CallbackContext context)
        // {
        //     RequestAction(CharacterClass.Skill1.ActionID, SkillTriggerStyle.MouseClick);
        // }

        public void InputSkill1(InputAction.CallbackContext context)
        {
            if(context.started && CharacterClass.Skill1)
            {
                RequestAction(actionState1.actionID, SkillTriggerStyle.Keyboard);
            }
            else if (context.canceled && CharacterClass.Skill1)
            {
                RequestAction(actionState1.actionID, SkillTriggerStyle.KeyboardRelease);
            }
        }

        public void InputSkill2 (InputAction.CallbackContext context)
        {
            if(context.started && CharacterClass.Skill2)
            {
                RequestAction(actionState2.actionID, SkillTriggerStyle.Keyboard);
            }
            else if(context.canceled && CharacterClass.Skill1)
            {
                RequestAction(actionState2.actionID, SkillTriggerStyle.KeyboardRelease);
            }
        }

        public void InputSkill3(InputAction.CallbackContext context)
        {
            if(context.started && CharacterClass.Skill3)
            {
                RequestAction(actionState3.actionID, SkillTriggerStyle.Keyboard);
            }
            else if(context.canceled && CharacterClass.Skill3)
            {
                RequestAction(actionState3.actionID, SkillTriggerStyle.KeyboardRelease);
            }
        }

        public void InputEmote1(InputAction.CallbackContext context)
        {
            if(context.started)
            {
                RequestAction(GameDataSource.Instance.Emote1ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            }
        }

        public void InputEmote2(InputAction.CallbackContext context)
        {
            if(context.started)
            {
                RequestAction(GameDataSource.Instance.Emote2ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            }
        }

        public void InputEmote3(InputAction.CallbackContext context)
        {
            if(context.started)
            {
                RequestAction(GameDataSource.Instance.Emote3ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            }
        }

        public void InputEmote4(InputAction.CallbackContext context)
        {
            if(context.started)
            {
                RequestAction(GameDataSource.Instance.Emote4ActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
            }
        }

        //For rotating the character. We set up a request action. We will find out what that means so we can implement more information here
        //Here is what we will try to do
        //  Initate a move request.
        //
        // public void InputRotateCharacter(InputAction.CallbackContext context)
        // {
        //     // if(context.performed)
        //     // {
        //         // RequestAction(GameDataSource.Instance.GeneralTargetActionPrototype.ActionID, SkillTriggerStyle.Keyboard);
  
        //         // m_RotateRequest = true;

        //         // Vector2 inputVector = context.ReadValue<Vector2>();
        //         // //Vector3 newSpeed = inputVector;

        //         // m_ServerCharacter.ServerSendRotation(inputVector);
        //     // }
        //     // else
        //     // {
        //     //     m_RotateRequest = false;
        //     // }
        // }

        //for moving the character. This is what we will do
        //when we are moving the character by holding down the move button,
        //we will shoot a ray from an empty gameobject that will blast a ray to the floor
        //we will try to replicate the raycast based movement
        public void InputMoveCharacter(InputAction.CallbackContext context)
        {
            // if(m_CurrentSkillInput == null)
            // {
            //     if(!EventSystem.current.IsPointerOverGameObject())
            //     {
                    if(context.performed)
                    {
                        RequestAction(GameDataSource.Instance.GeneralTargetActionPrototype.ActionID, SkillTriggerStyle.Keyboard);

                        m_ControllerMovement = true;

                        controllerRayObjectLight.intensity = 10;
                        m_MoveRequest = true;
                
                        //Debug.Log(context);
                    }
            //     }
            // }
        }  

#endregion        

        void UpdateAction1()
        {
            var isHoldingNetworkObject =
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(m_ServerCharacter.HeldNetworkObject.Value,
                    out var heldNetworkObject);

            NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(m_ServerCharacter.TargetId.Value,
                out var selection);

            var isSelectable = true;
            if (isHoldingNetworkObject)
            {
                // show drop!

                actionState1.actionID = GameDataSource.Instance.DropActionPrototype.ActionID;
            }
            else if ((m_ServerCharacter.TargetId.Value != 0
                    && selection != null
                    && selection.TryGetComponent(out PickUpState pickUpState))
               )
            {
                // special case: targeting a pickup-able item or holding a pickup object

                actionState1.actionID = GameDataSource.Instance.PickUpActionPrototype.ActionID;
            }
            else if (m_ServerCharacter.TargetId.Value != 0
                && selection != null
                && selection.NetworkObjectId != m_ServerCharacter.NetworkObjectId
                && selection.TryGetComponent(out ServerCharacter charState)
                && !charState.IsNpc)
            {
                // special case: when we have a player selected, we change the meaning of the basic action
                // we have another player selected! In that case we want to reflect that our basic Action is a Revive, not an attack!
                // But we need to know if the player is alive... if so, the button should be disabled (for better player communication)

                actionState1.actionID = GameDataSource.Instance.ReviveActionPrototype.ActionID;
                isSelectable = charState.NetLifeState.LifeState.Value != LifeState.Alive;
            }
            else
            {
                actionState1.SetActionState(CharacterClass.Skill1.ActionID);
            }

            actionState1.selectable = isSelectable;

            action1ModifiedCallback?.Invoke();
        }
        


        public class ActionState
        {
            public ActionID actionID { get; internal set; }

            public bool selectable { get; internal set; }

            internal void SetActionState(ActionID newActionID, bool isSelectable = true)
            {
                actionID = newActionID;
                selectable = isSelectable;
            }
        }
    }
}
