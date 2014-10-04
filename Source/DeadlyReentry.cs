﻿using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEngine;
using KSP;

namespace DeadlyReentry
{
	public class ModuleAeroReentry: PartModule
	{
        public const float CTOK = 273.15f;

        protected bool isCompatible = true;


		UIPartActionWindow _myWindow = null; 
		UIPartActionWindow myWindow {
			get {
				if(_myWindow == null) {
					foreach(UIPartActionWindow window in FindObjectsOfType (typeof(UIPartActionWindow))) {
						if(window.part == part) _myWindow = window;
					}
				}
				return _myWindow;
			}
		}

		static string FormatTime(double time)
		{
			int iTime = (int) time % 3600;
			int seconds = iTime % 60;
			int minutes = (iTime / 60) % 60;
			int hours = (iTime / 3600);
			return hours.ToString ("D2") 
				   + ":" + minutes.ToString ("D2") + ":" + seconds.ToString ("D2");
		}

		public static void PlaySound(FXGroup fx, float volume)
		{
			if(fx.audio.isPlaying) {
				if(fx.audio.volume < volume)
					fx.audio.volume = volume;
			} else {
				fx.audio.volume = volume;
				fx.audio.Play ();
			}
			if(ReentryPhysics.debugging)
				print (fx.audio.clip.name);

		}
		FXGroup _gForceFX = null;
		FXGroup gForceFX {
			get {
				if(_gForceFX == null) {
					_gForceFX = new FXGroup (part.partName + "_Crushing");
					_gForceFX.audio = gameObject.AddComponent<AudioSource>();
					_gForceFX.audio.clip = GameDatabase.Instance.GetAudioClip("DeadlyReentry/Sounds/gforce_damage");
					_gForceFX.audio.volume = GameSettings.SHIP_VOLUME;
					_gForceFX.audio.Stop ();
				}
				return _gForceFX;

			}
		}
		FXGroup _ablationSmokeFX = null;
		FXGroup ablationSmokeFX {
			get {
				if(_ablationSmokeFX == null) {
					_ablationSmokeFX = new FXGroup (part.partName + "_Smoking");
					_ablationSmokeFX.fxEmitters.Add (Emitter("fx_smokeTrail_medium").GetComponent<ParticleEmitter>());
				}
				return _ablationSmokeFX;
			}
		}

		FXGroup _ablationFX = null;
		FXGroup ablationFX {
			get {
				if(_ablationFX == null) {
					_ablationFX = new FXGroup (part.partName + "_Burning");
					_ablationFX.fxEmitters.Add (Emitter("fx_exhaustFlame_yellow").GetComponent<ParticleEmitter>());
                    _ablationFX.fxEmitters.Add(Emitter("fx_exhaustSparks_yellow").GetComponent<ParticleEmitter>());
					_ablationFX.audio = gameObject.AddComponent<AudioSource>();
					_ablationFX.audio.clip = GameDatabase.Instance.GetAudioClip("DeadlyReentry/Sounds/fire_damage");
                    _ablationFX.audio.volume = GameSettings.SHIP_VOLUME;
					_ablationFX.audio.Stop ();

				}
				return _ablationFX;
			}
		}

		[KSPField(isPersistant = false, guiActive = false, guiName = "Shockwave", guiUnits = "",   guiFormat = "G")]
		public string displayShockwave;

		[KSPField(isPersistant = false, guiActive = true, guiName = "Temperature", guiUnits = "C",   guiFormat = "F0")]
		public float displayTemperature;

		[KSPField(isPersistant = false, guiActive = false, guiName = "Acceleration", guiUnits = "G",   guiFormat = "F3")]
		public float displayGForce;

		[KSPField(isPersistant = false, guiActive = false, guiName = "Damage", guiUnits = "",   guiFormat = "G")]
		public string displayDamage;

        [KSPField(isPersistant = false, guiActive = false, guiName = "Cumulative G", guiUnits = "", guiFormat = "F0")]
        public double gExperienced = 0;

		[KSPField(isPersistant = true)]
		public float adjustCollider = 0;

		[KSPField(isPersistant = true)]
		public float crashTolerance = 8;

		[KSPField(isPersistant = true)]
		public float damage = 0;

		[KSPField(isPersistant = true)]
		public bool dead = false;

        [KSPField]
        public float gTolerance = -1;

        // counters etc
		private double lastGForce = 0;
        public static double crewGClamp = 30;
        public static double crewGPower = 4;
        public static double crewGMin = 5;
        public static double crewGWarn = 300000;
        public static double crewGLimit = 600000;
        public static double crewGKillChance = 0.75;

        protected float deltaTime = 0f;
        [KSPField(isPersistant = false, guiActive = true, guiName = "Ambient", guiUnits = "C", guiFormat = "F2")]
        protected float ambient = 0f; // ambient temperature (C)
        protected float density = 1.225f; // ambient density (kg/m^3)
        protected float shockwave; // shockwave temperature (C)
        protected Vector3 velocity; // velocity vector in local reference space (m/s)
        protected float speed; // velocity magnitude (m/s)
        private double counter = 0;

        private bool is_debugging = false;
        private bool is_on_fire = false;
        private bool is_gforce_fx_playing = false;

        private bool is_engine = false;
        private bool is_eva = false;
        private ModuleParachute parachute = null;
        private PartModule realChute = null;
        private Type rCType = null;
        private bool hasParachute = false;

        protected PartModule FARPartModule = null;
        protected FieldInfo FARField = null;

		[KSPEvent (guiName = "No Damage", guiActiveUnfocused = true, externalToEVAOnly = true, guiActive = false, unfocusedRange = 4f)]
		public void RepairDamage()
		{
			if (damage > 0) {
				damage = damage - UnityEngine.Random.Range (0.0f, 0.1f);
				if(damage < 0)
					damage = 0;
			}
			SetDamageLabel ();
			if (myWindow != null)
				myWindow.displayDirty = true;
		}

		public void SetDamageLabel() 
		{
            if (Events == null)
                return;
            try
            {
                if (damage > 0.5)
                    Events[""].guiName = "Repair Critical Damage";
                else if (damage > 0.25)
                    Events["RepairDamage"].guiName = "Repair Heavy Damage";
                else if (damage > 0.125)
                    Events["RepairDamage"].guiName = "Repair Moderate Damage";
                else if (damage > 0)
                    Events["RepairDamage"].guiName = "Repair Light Damage";
                else
                    Events["RepairDamage"].guiName = "No Damage";
            }
            catch
            {
            }

		}

        public override void OnAwake()
        {
            base.OnAwake();
            if (!CompatibilityChecker.IsCompatible())
            {
                isCompatible = false;
                return;
            }
            if (part && part.Modules != null) // thanks, FlowerChild!
            {
                is_engine = (part.Modules.Contains("ModuleEngines") || part.Modules.Contains("ModuleEnginesFX"));
                is_eva = part.Modules.Contains("KerbalEVA");
                if (part.Modules.Contains("ModuleParachute"))
                    parachute = (ModuleParachute)part.Modules["ModuleParachute"];
                if (part.Modules.Contains("RealChuteModule"))
                {
                    realChute = part.Modules["RealChuteModule"];
                    rCType = realChute.GetType();
                }
                hasParachute = (parachute != null) || (realChute != null);
            }
        }

		private bool GetShieldedStateFromFAR()
        {
            // Check if this part is shielded by fairings/cargobays according to FAR's information...
			if ((object)FARPartModule != null)
			{
				//Debug.Log("[DREC] Part has FAR module.");
				try
				{
					bool isShieldedFromFAR = ((bool)(FARField.GetValue(FARPartModule)));
					//Debug.Log("[DREC] Found FAR isShielded: " + isShieldedFromFAR.ToString());
					return isShieldedFromFAR;
				}
				catch (Exception e)
				{
					Debug.Log("[DREC]: " + e.Message);
					return false;
				}
			}
			else
			{
				//Debug.Log("[DREC] No FAR module.");
				return false;
			}
		}

		public override void OnStart (StartState state)
		{
            if (!isCompatible)
                return;
            counter = 0;
			if (state == StartState.Editor)
				return;
			SetDamageLabel ();
			if (myWindow != null)
				myWindow.displayDirty = true;
			// moved part detection logic to OnAWake
            // exception: FAR
            if (part.Modules.Contains("FARBasicDragModel"))
            {
                FARPartModule = part.Modules["FARBasicDragModel"];
            }
            else if (part.Modules.Contains("FARWingAerodynamicModel"))
            {
                FARPartModule = part.Modules["FARWingAerodynamicModel"];
            }
            if ((object)FARPartModule != null)
                FARField = FARPartModule.GetType().GetField("isShielded");

		}
		public virtual float AdjustedHeat(float temp)
		{
			return temp;
		}

		public bool IsShielded(Vector3 direction)
		{   
            if (GetShieldedStateFromFAR() == true)
            	return true;
            
            Ray ray = new Ray(part.transform.position + direction.normalized * (1.0f+adjustCollider), direction.normalized);
			RaycastHit[] hits = Physics.RaycastAll (ray, 10);
			foreach (RaycastHit hit in hits) {
				if(hit.rigidbody != null && hit.collider != part.collider) {
					return true;
				}
			}
			return false;
		}


        public float ReentryHeat()
        {
            if ((object)vessel == null || (object)vessel.flightIntegrator == null)
                return 0;
            shockwave = speed - CTOK;
            if (shockwave > 0)
            {
                shockwave = Mathf.Pow(shockwave, ReentryPhysics.shockwaveExponent);
                shockwave *= ReentryPhysics.shockwaveMultiplier;
            }
            ambient = vessel.flightIntegrator.getExternalTemperature();
            if (shockwave < ambient)
            {
                shockwave = ambient;
                if (is_debugging)
                    displayShockwave = shockwave.ToString("F0") + "C (Ambient)";
            }
            else if (density == 0)
            {
                shockwave = 0;
                displayShockwave = "None (vacuum)";
            }
            else
            {
                // deal with parachutes here
                if (hasParachute)
                {
                    bool cut = ambient + Math.Pow(density, ReentryPhysics.densityExponent) * shockwave * 10f
                    > part.maxTemp * ReentryPhysics.parachuteTempMult;
                    if ((object)parachute != null)
                    {
                        ModuleParachute p = parachute;
                        if (p.deploymentState == ModuleParachute.deploymentStates.DEPLOYED || p.deploymentState == ModuleParachute.deploymentStates.SEMIDEPLOYED)
                            p.CutParachute();
                    }
                    if ((object)realChute != null)
                    {
                        if (!(bool)rCType.GetProperty("anyDeployed").GetValue(realChute, null))
                            rCType.GetMethod("GUICut").Invoke(realChute, null);
                    }
                }
                if (IsShielded(velocity))
                    displayShockwave = "Shielded";
                else
                {
                    if (is_debugging)
                        displayShockwave = shockwave.ToString("F0") + "C";
                    return AdjustedHeat(ReentryPhysics.TemperatureDelta(density, shockwave + CTOK, part.temperature + CTOK));
                }
            }
            return 0;
        }

		public void FixedUpdate ()
		{
            if (!HighLogic.LoadedSceneIsFlight || !isCompatible)
                return;
			Rigidbody rb = part.Rigidbody;
            deltaTime = TimeWarp.fixedDeltaTime;
            density = (float)(part.vessel.staticPressure * 101325 / (287.058 * (part.vessel.flightIntegrator.getExternalTemperature() + CTOK)));
            // calc here again, just in case. ReentryPhysics.frameDensity; // close enough

			if (!rb || part.physicalSignificance == Part.PhysicalSignificance.NONE)
				return;

			if (is_debugging != ReentryPhysics.debugging)
			{
				is_debugging = ReentryPhysics.debugging;
				Fields ["displayShockwave"].guiActive = ReentryPhysics.debugging;
				Fields ["displayGForce"].guiActive = ReentryPhysics.debugging;
	            Fields["gExperienced"].guiActive = ReentryPhysics.debugging;
			}

            // fix for parts that animate/etc: all parts will use vessel velocity.
            velocity = part.vessel.orbit.GetVel() - part.vessel.mainBody.getRFrmVel(part.vessel.vesselTransform.position);
            speed = velocity.magnitude;

            if (counter < 5.0)
            {
                counter += deltaTime;
                lastGForce = 0;
                return;
            }

			part.temperature += ReentryHeat();
            if (part.temperature < -CTOK) // clamp to Absolute Zero
                part.temperature = -CTOK;
			displayTemperature = part.temperature;
			CheckForFire();
			CheckGeeForces();


		}

		public void AddDamage(float dmg)
		{
			if (dead || part == null || part.partInfo == null || part.partInfo.partPrefab == null)
				return;
			if(ReentryPhysics.debugging)
				print (part.partInfo.title + ": +" + dmg + " damage");
			damage += dmg;
			part.maxTemp = part.partInfo.partPrefab.maxTemp * (1 - 0.15f * damage);
			part.breakingForce = part.partInfo.partPrefab.breakingForce * (1 - damage);
			part.breakingTorque = part.partInfo.partPrefab.breakingTorque * (1 - damage);
			part.crashTolerance = part.partInfo.partPrefab.crashTolerance * (1 - 0.5f * damage);
			SetDamageLabel ();
		}
		public void CheckGeeForces()
		{
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready)
            {
                if (dead || (object)vessel == null || deltaTime > 0.5 || deltaTime <= 0)
                    return; // don't check G-forces in warp

                double geeForce = vessel.geeForce_immediate;
                if (geeForce > 40 && geeForce > lastGForce)
                {
                    // G forces over 40 are probably a Kraken twitch unless they last multiple frames
                    displayGForce = displayGForce * (1 - deltaTime) + (float)(lastGForce * deltaTime);
                }
                else
                {
                    //keep a running average of G force over 1s, to further prevent absurd spikes (mostly decouplers & parachutes)
                    displayGForce = displayGForce * (1 - deltaTime) + (float)(geeForce * deltaTime);
                }
                if (displayGForce < crewGMin)
                    gExperienced = 0;

                //double gTolerance;
                if (gTolerance < 0)
                {
                    if (is_engine && damage < 1)
                        gTolerance = (float)Math.Pow(UnityEngine.Random.Range(11.9f, 12.1f) * part.crashTolerance, 0.5);
                    else
                        gTolerance = (float)Math.Pow(UnityEngine.Random.Range(5.9f, 6.1f) * part.crashTolerance, 0.5);

                    gTolerance *= ReentryPhysics.gToleranceMult;
                }
                if (gTolerance >= 0 && displayGForce > gTolerance)
                { // G tolerance is based roughly on crashTolerance
                    AddDamage(deltaTime * (float)(displayGForce / gTolerance - 1));
                    if (!is_eva)
                    { // kerbal bones shouldn't sound like metal when they break.
                        gForceFX.audio.pitch = (float)(displayGForce / gTolerance);
                        PlaySound(gForceFX, damage * 0.3f + 0.7f);
                        is_gforce_fx_playing = true;
                    }
                }
                else if (is_gforce_fx_playing)
                {
                    float new_volume = (gForceFX.audio.volume *= 0.8f);
                    if (new_volume < 0.001f)
                    {
                        gForceFX.audio.Stop();
                        is_gforce_fx_playing = false;
                    }
                }
                if (damage >= 1.0f && !dead)
                {
                    dead = true;
                    FlightLogger.eventLog.Add("[" + FormatTime(vessel.missionTime) + "] "
                                                + part.partInfo.title + " exceeded g-force tolerance.");
                    part.explode();
                    return;
                }
                if (Math.Max(displayGForce, geeForce) >= crewGMin)
                {
                    gExperienced += Math.Pow(Math.Min(Math.Abs(Math.Max(displayGForce, geeForce)), crewGClamp), crewGPower) * deltaTime;
                    List<ProtoCrewMember> crew = part.protoModuleCrew; //vessel.GetVesselCrew();
                    if (gExperienced > crewGWarn && crew.Count > 0)
                    {
                            
                        if (gExperienced < crewGLimit)
                                ScreenMessages.PostScreenMessage("Reaching Crew G limit!", 3f, ScreenMessageStyle.UPPER_CENTER);
                        else
                        {
                            // borrowed from TAC Life Support
                            if (UnityEngine.Random.Range(0, 1) < crewGKillChance)
                            {
                                int crewMemberIndex = UnityEngine.Random.Range(0, crew.Count - 1);
                                if (CameraManager.Instance.currentCameraMode == CameraManager.CameraMode.IVA)
                                {
                                    CameraManager.Instance.SetCameraFlight();
                                }
								ProtoCrewMember member = crew[crewMemberIndex];

                                ScreenMessages.PostScreenMessage(vessel.vesselName + ": Crewmember " + member.name + " died of G-force damage!", 30.0f, ScreenMessageStyle.UPPER_CENTER);
                                FlightLogger.eventLog.Add("[" + FormatTime(vessel.missionTime) + "] "
                                                + member.name + " died of G-force damage.");
                                Debug.Log("*DRE* [" + Time.time + "]: " + vessel.vesselName + " - " + member.name + " died of G-force damage.");

                                if (!vessel.isEVA)
                                {
                                    part.RemoveCrewmember(member);
                                    member.Die();
                                }
                            }
                        }
                    }
                }
                lastGForce = vessel.geeForce_immediate;
            }
		}

		public void CheckForFire()
		{
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ready)
            {
                if (FlightGlobals.ActiveVessel.situation != Vessel.Situations.PRELAUNCH || FlightGlobals.ActiveVessel.missionTime > 2.0)
                {
                    if (dead)
                        return;
                    float damageThreshold;

                    if (is_engine && damage < 1)
                        damageThreshold = part.maxTemp * 0.975f;
                    else if (is_eva)
                    {
                        damageThreshold = 800 * (1 - damage) * (1 - damage) - CTOK;
                        part.maxTemp = 900;
                    }
                    else
                        damageThreshold = part.maxTemp * 0.85f;
                    if (part.temperature > damageThreshold)
                    {
                        // Handle client-side fire stuff.
                        // OH GOD IT'S ON FIRE.
                        float tempRatio = (part.temperature / damageThreshold) - 1f;
                        tempRatio *= (part.temperature / part.maxTemp) * (part.temperature / part.maxTemp) * 4f;
                        AddDamage(deltaTime * (damage + 1.0f) * tempRatio);
                        float soundTempRatio = part.temperature / part.maxTemp;
                        PlaySound(ablationFX, soundTempRatio * soundTempRatio);

                        if (is_engine && damage < 1)
                            part.temperature = UnityEngine.Random.Range(0.97f + 0.05f * damage, 0.98f + 0.05f * damage) * part.maxTemp;
                        else if (damage < 1)// non-engines can keep burning
                            part.temperature += UnityEngine.Random.Range(0.5f + 0.5f * damage, 1.0f + 0.5f * damage) * (tempRatio * 0.04f * part.maxTemp * deltaTime);

                        if (part.temperature > part.maxTemp || damage >= 1.0f)
                        { // has it burnt up completely?
                            foreach (ParticleEmitter fx in ablationFX.fxEmitters)
                                GameObject.DestroyImmediate(fx.gameObject);
                            foreach (ParticleEmitter fx in ablationSmokeFX.fxEmitters)
                                GameObject.DestroyImmediate(fx.gameObject);
                            if (!dead)
                            {
                                dead = true;
                                FlightLogger.eventLog.Add("[" + FormatTime(vessel.missionTime) + "] "
                                                           + part.partInfo.title + " burned up from overheating.");
                                part.explode();
                                return;
                            }
                        }
                        else
                        {
                            is_on_fire = true;
                            foreach (ParticleEmitter fx in ablationFX.fxEmitters)
                            {
                                fx.gameObject.SetActive(true);
                                fx.gameObject.transform.LookAt(part.transform.position + velocity);
                                fx.gameObject.transform.Rotate(90, 0, 0);
                            }
                            foreach (ParticleEmitter fx in ablationSmokeFX.fxEmitters)
                            {
                                fx.gameObject.SetActive(density > 0.02);
                                fx.gameObject.transform.LookAt(part.transform.position + velocity);
                                fx.gameObject.transform.Rotate(90, 0, 0);
                            }
                        }
                    }
                    else if (is_on_fire)
                    { // not on fire.
                        is_on_fire = false;
                        foreach (ParticleEmitter fx in ablationFX.fxEmitters)
                            fx.gameObject.SetActive(false);
                        foreach (ParticleEmitter fx in ablationSmokeFX.fxEmitters)
                            fx.gameObject.SetActive(false);

                    }
                }
            }
		}
		public GameObject Emitter(string fxName) {
			GameObject fx = (GameObject)UnityEngine.Object.Instantiate (UnityEngine.Resources.Load ("Effects/" + fxName));

			fx.transform.parent = part.transform;
			fx.transform.localPosition = new Vector3 (0, 0, 0);
			fx.transform.localRotation = Quaternion.identity;
			fx.SetActive (false);
			return fx;

		}

	}

	public class ModuleHeatShield: ModuleAeroReentry
	{
		/* Sample configNode:

	MODULE
	{
		name = ModuleHeatShield
		direction = 0, -1, 0 // bottom of pod
		reflective = 0.05 // 5% of heat is ignored at correct angle
		ablative = AblativeShielding
		loss
		{ // loss is based on the shockwave temperature
			key = 450 0 // start ablating at 450 degrees C
			key = 550 2 // peak ablation at 550 degrees C
			key = 3000 2.5 // max ablation at 3000 degrees C
		}
		dissipation
		{ // dissipation is based on the part's current temperature
				key = 300 0 // begin ablating at 300 degrees C
				key = 500 330 // maximum dissipation at 500 degrees C
		}
	}
	
	RESOURCE
	{
		name = AblativeShielding
		amount = 250
		maxAmount = 250
	}
	
	*/

		[KSPField(isPersistant = false, guiActive = false, guiName = "angle", guiUnits = " ", guiFormat = "F3")]
		public float dot;


		[KSPField(isPersistant = true)]
		public int deployAnimationController;

		[KSPField(isPersistant = true)]
		public Vector3 direction;

		[KSPField(isPersistant = true)]
		public float reflective;

		[KSPField(isPersistant = true)]
		public string ablative;

		[KSPField(isPersistant = true)]
		public float area;

		[KSPField(isPersistant = true)]
		public float thickness;

		[KSPField(isPersistant = true)]
		public FloatCurve loss = new FloatCurve();

		[KSPField(isPersistant = true)]
		public FloatCurve dissipation = new FloatCurve();

		public override void OnStart (StartState state)
		{
			base.OnStart (state);
			if (ablative == null)
				ablative = "None";
		}
		public override string GetInfo()
		{
			string s = "Active Heat Shield";
			if (direction.x != 0 || direction.y != 0 || direction.z != 0)
				s += " (directional)";
			return s;
		}

		public override float AdjustedHeat(float temp)
		{
			if (direction.magnitude == 0) // an empty vector means the shielding exists on all sides
				dot = 1; 
			else // check the angle between the shock front and the shield
				dot = Vector3.Dot (velocity.normalized, part.transform.TransformDirection(direction).normalized);
			
			if (dot > 0 && temp > 0) {
				//radiate away some heat
				float rad = temp * dot * reflective;
				temp -= rad  * (1 - damage) * (1 - damage);
				if(loss.Evaluate(shockwave) > 0
				   && part.Resources.Contains (ablative)) {
					// ablate away some shielding
					float ablation = (float) (dot 
					                          * loss.Evaluate((float) Math.Pow (shockwave, ReentryPhysics.temperatureExponent)) 
					                          * Math.Pow (density, ReentryPhysics.densityExponent) 
					                          * deltaTime);

                    float disAmount = dissipation.Evaluate(part.temperature) * ablation * (1 - damage) * (1 - damage);
                    if (disAmount > 0)
                    {
                        if (part.Resources[ablative].amount < ablation)
                            ablation = (float)part.Resources[ablative].amount;
                        // wick away some heat with the shielding
                        part.Resources[ablative].amount -= ablation;
                        temp -= dissipation.Evaluate(part.temperature) * ablation * (1 - damage) * (1 - damage);
                    }
				}
			}
			return temp;
		}
	}

	[KSPAddon(KSPAddon.Startup.MainMenu, false)] // fixed
	public class FixMaxTemps : MonoBehaviour
	{
		public void Start()
		{
            if (!CompatibilityChecker.IsCompatible())
                return;

			foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS")) {
				if(node.HasValue("ridiculousMaxTemp")) {
					float maxTemp;
					float scale = 0.5f;
					if(node.HasValue ("maxTempScale"))
						float.TryParse(node.GetValue("maxTempScale"), out scale);
					if(scale > 0 && float.TryParse(node.GetValue("ridiculousMaxTemp"), out maxTemp)) {
                        if (PartLoader.LoadedPartsList != null)
                        {
                            foreach (AvailablePart part in PartLoader.LoadedPartsList)
                            {
                                try
                                {
                                    if (part.partPrefab != null && !part.partPrefab.Modules.Contains("ModuleHeatShield")) // allow heat sinks
                                    {
                                        float oldTemp = part.partPrefab.maxTemp;
                                        bool changed = false;
                                        if (part.partPrefab.maxTemp > maxTemp)
                                        {
                                            part.partPrefab.maxTemp *= scale;
                                            changed = true;
                                        }
                                        if (part.partPrefab.maxTemp > maxTemp)
                                        {
                                            changed = true;
                                            part.partPrefab.maxTemp = maxTemp;
                                        }
                                        if (changed)
                                        {
                                            float curScale = part.partPrefab.maxTemp / oldTemp;

                                            foreach (ModuleEngines module in part.partPrefab.Modules.OfType<ModuleEngines>())
                                            {
                                                module.heatProduction *= curScale;
                                            }
                                            foreach (ModuleEnginesFX module in part.partPrefab.Modules.OfType<ModuleEnginesFX>())
                                            {
                                                module.heatProduction *= curScale;
                                            }
                                        }
                                    }
                                }
                                catch
                                {
                                }
                            }
                        }
					}
				}
			}
		}
	}

	[KSPAddon(KSPAddon.Startup.Flight, false)]
    public class ReentryPhysics : MonoBehaviour
    {
        protected bool isCompatible = true;
		private static AerodynamicsFX _afx;

        public static AerodynamicsFX afx {
			get {
				if (_afx == null) {
					GameObject fx = GameObject.Find ("FXLogic");
					if (fx != null) {
						_afx = fx.GetComponent<AerodynamicsFX> ();
					}
				}
				return _afx;
			}
		}

		public static Vector3 frameVelocity;
        public static float frameDensity = 0f;

        public static float shockwaveMultiplier = 1.0f;
        public static float shockwaveExponent = 1.0f;
        public static float heatMultiplier = 20.0f;
		public static float temperatureExponent = 1.0f;
		public static float densityExponent = 1.0f;

		public static float startThermal = 800.0f;
		public static float fullThermal = 1150.0f;
        public static float afxDensityExponent = 0.7f;

        public static float gToleranceMult = 6.0f;
        public static float parachuteTempMult = 0.25f;

        public static bool debugging = false;
        protected Rect windowPos = new Rect(100, 100, 0, 0);

		public static float TemperatureDelta(double density, float shockwaveK, float partTempK)
		{
			if (shockwaveK < partTempK || density == 0 || shockwaveK < 0)
				return 0;
			return (float) ( Math.Pow (Math.Abs(shockwaveK - partTempK), temperatureExponent) 
						   * Math.Pow (density, densityExponent)
						   * heatMultiplier * TimeWarp.fixedDeltaTime);
		}

		public void Start()
		{
            if (!CompatibilityChecker.IsCompatible())
            {
                isCompatible = false;
                return;
            }
            enabled = true; // 0.24 compatibility
			foreach (ConfigNode node in GameDatabase.Instance.GetConfigNodes ("REENTRY_EFFECTS")) {
                if(node.HasValue("shockwaveExponent"))
                    float.TryParse(node.GetValue("shockwaveExponent"), out shockwaveExponent);
                if (node.HasValue("shockwaveMultiplier"))
                    float.TryParse(node.GetValue("shockwaveMultiplier"), out shockwaveMultiplier);
                if(node.HasValue("heatMultiplier"))
					float.TryParse (node.GetValue ("heatMultiplier"), out heatMultiplier);
				if(node.HasValue("startThermal"))
					float.TryParse (node.GetValue ("startThermal"), out startThermal);
				if(node.HasValue("fullThermal"))
					float.TryParse (node.GetValue ("fullThermal"), out fullThermal);
                if (node.HasValue("afxDensityExponent"))
                    float.TryParse(node.GetValue("afxDensityExponent"), out afxDensityExponent);
				if(node.HasValue("temperatureExponent"))
					float.TryParse (node.GetValue ("temperatureExponent"), out temperatureExponent);
				if(node.HasValue("densityExponent"))
					float.TryParse (node.GetValue ("densityExponent"), out densityExponent);

                if (node.HasValue("gToleranceMult"))
                    float.TryParse(node.GetValue("gToleranceMult"), out gToleranceMult);

                if (node.HasValue("parachuteTempMult"))
                    float.TryParse(node.GetValue("parachuteTempMult"), out parachuteTempMult);


                if (node.HasValue("crewGClamp"))
                    double.TryParse(node.GetValue("crewGClamp"), out ModuleAeroReentry.crewGClamp);
                if (node.HasValue("crewGPower"))
                    double.TryParse(node.GetValue("crewGPower"), out ModuleAeroReentry.crewGPower);
                if (node.HasValue("crewGMin"))
                    double.TryParse(node.GetValue("crewGMin"), out ModuleAeroReentry.crewGMin);
                if (node.HasValue("crewGWarn"))
                    double.TryParse(node.GetValue("crewGWarn"), out ModuleAeroReentry.crewGWarn);
                if (node.HasValue("crewGLimit"))
                    double.TryParse(node.GetValue("crewGLimit"), out ModuleAeroReentry.crewGLimit);
                if (node.HasValue("crewGKillChance"))
                    double.TryParse(node.GetValue("crewGKillChance"), out ModuleAeroReentry.crewGKillChance);


				if(node.HasValue("debugging"))
					bool.TryParse (node.GetValue ("debugging"), out debugging);
				break;
			};
		}

        public void OnGUI()
        {
            if (isCompatible && debugging)
            {
                windowPos = GUILayout.Window("DeadlyReentry".GetHashCode(), windowPos, DrawWindow, "Deadly Reentry 2.0 Setup");
            }
        }

		private void FixAeroFX(AerodynamicsFX aeroFX)
		{
            aeroFX.airDensity = (float)(Math.Pow(frameDensity, afxDensityExponent));
			if (afx.velocity.magnitude < startThermal) // approximate speed where shockwaves begin visibly glowing
				afx.state = 0;
			else if (afx.velocity.magnitude >= fullThermal)
				afx.state = 1;
			else
				afx.state = (afx.velocity.magnitude - startThermal) / (fullThermal - startThermal);
		}

		public void FixedUpdate()
		{
            if (!isCompatible)
                return;
			FixAeroFX (afx);
			frameVelocity = Krakensbane.GetFrameVelocityV3f() - Krakensbane.GetLastCorrection() * TimeWarp.fixedDeltaTime;
            if((object)FlightGlobals.ActiveVessel != null) // FIXME only valid for Earthlike atmospheres
                frameDensity = (float)(FlightGlobals.ActiveVessel.staticPressure *101325 / (287.058 * (FlightGlobals.ActiveVessel.flightIntegrator.getExternalTemperature() + 273.15)));

		}

		public void LateUpdate()
		{
            if (!isCompatible)
                return;
			FixAeroFX (afx);
		}

        public void Update()
        {
            if (!isCompatible)
                return;
            if (Input.GetKeyDown(KeyCode.R) && Input.GetKey(KeyCode.LeftAlt) && Input.GetKey(KeyCode.D))
            {
                debugging = !debugging;
            }

            if (FlightGlobals.ready)
            {
                if ((afx != null))
                {
					FixAeroFX(afx);
				
					foreach (Vessel vessel in FlightGlobals.Vessels) 
					{
						if(vessel.loaded)// && afx.FxScalar > 0)
						{
		                    foreach (Part p in vessel.Parts)
		                    {
                                if (!(p.Modules.Contains("ModuleAeroReentry") || p.Modules.Contains("ModuleHeatShield")))
                                    p.AddModule("ModuleAeroReentry"); // thanks a.g.!

							}
                        }
                    }
                }
            }
        }

        public void DrawWindow(int windowID)
        {
            GUIStyle buttonStyle = new GUIStyle(GUI.skin.button);
            buttonStyle.padding = new RectOffset(5, 5, 3, 0);
            buttonStyle.margin = new RectOffset(1, 1, 1, 1);
            buttonStyle.stretchWidth = false;
            buttonStyle.stretchHeight = false;

            GUIStyle labelStyle = new GUIStyle(GUI.skin.label);
            labelStyle.wordWrap = false;

            GUILayout.BeginVertical();

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("X", buttonStyle))
            {
                debugging = false;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shockwave Multiplier:", labelStyle);
            string newShockwaveMultiplier = GUILayout.TextField(shockwaveMultiplier.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Shockwave Exponent:", labelStyle);
            string newShockwaveExponent = GUILayout.TextField(shockwaveExponent.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Temperature Exponent:", labelStyle);
            string newTemperatureExponent = GUILayout.TextField(temperatureExponent.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			GUILayout.Label("Density Exponent:", labelStyle);
			string newDensityExponent = GUILayout.TextField(densityExponent.ToString(), GUILayout.MinWidth(100));
			GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Multiplier:", labelStyle);
            string newMultiplier = GUILayout.TextField(heatMultiplier.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			GUILayout.Label("F/X Transition", labelStyle);
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			GUILayout.Label("Begin at:", labelStyle);
			string newThermal = GUILayout.TextField(startThermal.ToString(), GUILayout.MinWidth(100));
			GUILayout.Label("m/s", labelStyle);
			GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			GUILayout.Label("Full at:", labelStyle);
			string newThermalFull = GUILayout.TextField(fullThermal.ToString(), GUILayout.MinWidth(100));
			GUILayout.Label("m/s", labelStyle);
			GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("FX Density exp.:", labelStyle);
            string newAFXDensityExponent = GUILayout.TextField(afxDensityExponent.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("G Tolerance Mult:", labelStyle);
            string newGToleranceMult = GUILayout.TextField(gToleranceMult.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Parachute Temp Mult:", labelStyle);
            string newParachuteTempMult = GUILayout.TextField(parachuteTempMult.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Max", labelStyle);
            string newcrewGClamp = GUILayout.TextField(ModuleAeroReentry.crewGClamp.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Exponent", labelStyle);
            string newcrewGPower = GUILayout.TextField(ModuleAeroReentry.crewGPower.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Min", labelStyle);
            string newcrewGMin = GUILayout.TextField(ModuleAeroReentry.crewGMin.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Warn Level", labelStyle);
            string newcrewGWarn = GUILayout.TextField(ModuleAeroReentry.crewGWarn.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Kill threshold", labelStyle);
            string newcrewGLimit = GUILayout.TextField(ModuleAeroReentry.crewGLimit.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            GUILayout.Label("Crew G Kill chance per update", labelStyle);
            string newcrewGKillChance = GUILayout.TextField(ModuleAeroReentry.crewGKillChance.ToString(), GUILayout.MinWidth(100));
            GUILayout.EndHorizontal();

			GUILayout.BeginHorizontal();
			if (GUILayout.Button ("Save")) {
				ConfigNode node = new ConfigNode("@REENTRY_EFFECTS[Default]:Final");
				ConfigNode savenode = new ConfigNode();
                node.AddValue ("@shockwaveExponent", shockwaveExponent.ToString());
                node.AddValue ("@shockwaveMultiplier", shockwaveMultiplier.ToString());
				node.AddValue ("@heatMultiplier", heatMultiplier.ToString ());
				node.AddValue ("@startThermal", startThermal.ToString ());
				node.AddValue ("@fullThermal", fullThermal.ToString ());
                node.AddValue("@afxDensityExponent", afxDensityExponent.ToString());
				node.AddValue ("@temperatureExponent", temperatureExponent.ToString ());
				node.AddValue ("@densityExponent", densityExponent.ToString ());
                node.AddValue("@gToleranceMult", gToleranceMult.ToString());
                node.AddValue("@parachuteTempMult", gToleranceMult.ToString());

                node.AddValue("@crewGClamp", ModuleAeroReentry.crewGClamp.ToString());
                node.AddValue("@crewGPower", ModuleAeroReentry.crewGPower.ToString());
                node.AddValue("@crewGMin", ModuleAeroReentry.crewGMin.ToString());
                node.AddValue("@crewGWarn", ModuleAeroReentry.crewGWarn.ToString());
                node.AddValue("@crewGLimit", ModuleAeroReentry.crewGLimit.ToString());
                node.AddValue("@crewGKillChance", ModuleAeroReentry.crewGKillChance.ToString());
				
                savenode.AddNode (node);
				savenode.Save (KSPUtil.ApplicationRootPath.Replace ("\\", "/") + "GameData/DeadlyReentry/custom.cfg");
			}
			GUILayout.EndHorizontal();

            GUILayout.EndVertical();

            GUI.DragWindow();

            if (GUI.changed)
            {
                float newValue;
                if (float.TryParse(newShockwaveMultiplier, out newValue))
                {
                    shockwaveMultiplier = newValue;
                }
                if (float.TryParse(newShockwaveExponent, out newValue))
                {
                    shockwaveExponent = newValue;
                }
                if (float.TryParse(newTemperatureExponent, out newValue))
                {
                    temperatureExponent = newValue;
                }

				if (float.TryParse(newDensityExponent, out newValue))
				{
					densityExponent = newValue;
				}

                if (float.TryParse(newMultiplier, out newValue))
                {
                    heatMultiplier = newValue;
                }
				if (float.TryParse(newThermal, out newValue))
				{
					startThermal = newValue;
				}
				if (float.TryParse(newThermalFull, out newValue))
				{
					fullThermal = newValue;
				}
                if (float.TryParse(newAFXDensityExponent, out newValue))
                {
                    afxDensityExponent = newValue;
                }
                if (float.TryParse(newGToleranceMult, out newValue))
				{
                    gToleranceMult = newValue;
				}
                if (float.TryParse(newParachuteTempMult, out newValue))
                {
                    parachuteTempMult = newValue;
                }

                if (float.TryParse(newcrewGClamp, out newValue))
                {
                    ModuleAeroReentry.crewGClamp = newValue;
                }

				if (float.TryParse(newcrewGPower, out newValue))
				{
					ModuleAeroReentry.crewGPower = newValue;
				}

                if (float.TryParse(newcrewGMin, out newValue))
                {
                    ModuleAeroReentry.crewGMin = newValue;
                }
				if (float.TryParse(newcrewGWarn, out newValue))
				{
					ModuleAeroReentry.crewGWarn = newValue;
				}
				if (float.TryParse(newcrewGLimit, out newValue))
				{
					ModuleAeroReentry.crewGLimit = newValue;
				}
                if (float.TryParse(newcrewGKillChance, out newValue))
				{
                    ModuleAeroReentry.crewGKillChance = newValue;
				}
			}
        }
    }
}