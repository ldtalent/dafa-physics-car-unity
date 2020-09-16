using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Global enum
public enum Faction {
    Player,
    Enemy,
    Neutral
};

public class DriftController : MonoBehaviour {

    #region Parameters
    public float Accel = 15.0f;         // In meters/second2
    public float Boost = 4f/3;          // In ratio
    public float TopSpeed = 30.0f;      // In meters/second
    //public float Jump = 3.0f;           // In meters/second2
    public float GripX = 12.0f;          // In meters/second2
    public float GripZ = 3.0f;          // In meters/second2
    public float Rotate = 190;       // In degree/second
    public float RotVel = 0.8f;         // Ratio of forward velocity transfered on rotation

    // Center of mass, fraction of collider boundaries (= half of size)
    // 0 = center, and +/-1 = edge in the pos/neg direction.
    public Vector3 CoM = new Vector3 (0f, .5f, 0f);
                                                
    
    public Faction carFaction = Faction.Player;  // Drop-down to select faction of this object
    public Transform Target;            // Target for the AI to act upon

    // Ground & air angular drag
    // reduce stumbling time on ground but maintain on-air one
    float AngDragG = 5.0f;
    float AngDragA = 0.05f;

    // Rotational
    float MinRotSpd = 1f;           // Velocity to start rotating
    float MaxRotSpd = 4f;           // Velocity to reach max rotation
    public AnimationCurve SlipL;    // Slip hysteresis static to full (x, input = speed)
    public AnimationCurve SlipU;    // Slip hysteresis full to static (y, output = slip ratio)
    public float SlipMod = 20f;     // Basically widens the slip curve
                                    // (determine the min speed to reach max slip)

    // AI-specific parameters
    [Header("AI Behaviors")]
    public float turnTh = 20f;      // Delta threshold to goal before start turning
    #endregion

    #region Intermediate
    Rigidbody rigidBody;
    Bounds groupCollider;
    float distToGround;

    // The actual value to be used (modification of parameters)
    float rotate;
    float accel;
    float gripX;
    float gripZ;
    float rotVel;
    float slip;     // The value used based on Slip curves

    // For determining drag direction
    float isRight = 1.0f;
    float isForward = 1.0f;

    bool isRotating = false;
    bool isGrounded = true;
    bool isStumbling = false;

    // Control signals
    float inThrottle = 0f;
    [HideInInspector] public float inTurn = 0f;
    bool inReset = false;
    bool isStuck = false;
    bool autoReset = false;
    bool inBoost = false;
    bool inSlip = false;

    Vector3 spawnP;
    Quaternion spawnR;
    
    Vector3 vel = new Vector3(0f, 0f, 0f);
    Vector3 pvel = new Vector3(0f, 0f, 0f);
    #endregion



    // Use this for initialization
    void Start () {
		rigidBody = GetComponent<Rigidbody>();

        // Store start location & rotation
        spawnP = transform.position;
        spawnR = transform.rotation;
        
        groupCollider = GetBounds(gameObject);     // Get the full collider boundary of group
        distToGround = groupCollider.extents.y;    // Pivot to the outermost collider

        // Move the CoM to a fraction of colliders boundaries
        rigidBody.centerOfMass = Vector3.Scale(groupCollider.extents, CoM);

        //distToGround = transform.position.y + 1f;
    }

    // Called once per frame
    void Update() {
        Debug.DrawRay(transform.position, rigidBody.velocity / 2, Color.green);
        
        // Reset to spawn if out of bounds
        if (transform.position.y < -10) {
            transform.position = spawnP;
            transform.rotation = spawnR;
            inReset = true;
        }
    }

    // Called once multiple times per frame 
    // (according to physics setting)
    void FixedUpdate() {
        #region Situational Checks
        accel = Accel;
        rotate = Rotate;
        gripX = GripX;
        gripZ = GripZ;
        rotVel = RotVel;
        rigidBody.angularDrag = AngDragG;

        // Adjustment in slope
        accel = accel * Mathf.Cos(transform.eulerAngles.x * Mathf.Deg2Rad);
        accel = accel > 0f ? accel : 0f;
        gripZ = gripZ * Mathf.Cos(transform.eulerAngles.x * Mathf.Deg2Rad);
        gripZ = gripZ > 0f ? gripZ : 0f;
        gripX = gripX * Mathf.Cos(transform.eulerAngles.z * Mathf.Deg2Rad);
        gripX = gripX > 0f ? gripX : 0f;

        // A short raycast to check below
        isGrounded = Physics.Raycast(transform.position, -transform.up, distToGround + 0.1f);
        if (!isGrounded) {
            rotate = 0f;
            accel = 0f;
            gripX = 0f;
            gripZ = 0f;
            rigidBody.angularDrag = AngDragA;
        }

        // Prevent the rotational input intervenes with physics angular velocity 
        isStumbling = rigidBody.angularVelocity.magnitude > 0.1f * Rotate * Time.deltaTime;
        if (isStumbling) {
            //rotate = 0f;
        }

        // Start turning only if there's velocity
        if (pvel.magnitude < MinRotSpd) {
            rotate = 0f;
        } else {
            rotate = pvel.magnitude / MaxRotSpd * rotate;
        }

        if (rotate > Rotate) rotate = Rotate;

        // Calculate grip based on sideway velocity in hysteresis curve
        if (!inSlip) {
            // Normal => slip
            slip = this.SlipL.Evaluate(Mathf.Abs(pvel.x)/SlipMod);
            if (slip == 1f) inSlip = true;
        } else {
            // Slip => Normal
            slip = this.SlipU.Evaluate(Mathf.Abs(pvel.x)/SlipMod);
            if (slip == 0f) inSlip = false;
        }

        DebugPlayer(slip);

        //rotate *= (1f + 0.5f * slip);   // Overall rotation, (body + vector)
        rotate *= (1f - 0.3f * slip);   // Overall rotation, (body + vector)
        rotVel *= (1f - slip);          // The vector modifier (just vector)

        /* Should be:
         * 1. Moving fast       : local forward, world forward.
         * 2. Swerve left       : instantly rotate left, local sideways, world forward.
         * 3. Wheels turn a little : small adjustments to the drifting arc.
         * 3. Wheels turn right : everything the same, traction still gone.
         * 4. Slowing down      : instantly rotate right, local forward, world left.
         * 
         * Update, solution: Hysteresis, gradual loss but snappy return.
         */

        #endregion

        #region Logics
        // Get command from keyboard or simple AI (conditional rulesets)
        switch (carFaction) {
            case Faction.Player:
                InputKeyboard();
                break;
            case Faction.Enemy:
                InputEnemy();    // Chase player
                autoReset = true;
                break;
            case Faction.Neutral:
                inThrottle = 1f; // Just straight
                autoReset = true;
                break;
            default:
                // Do nothing
                break;
        }

        // Execute the commands
        Controller();   // pvel assigment in here
        #endregion

        #region Passives
        // Get the local-axis velocity after rotation
        vel = transform.InverseTransformDirection(rigidBody.velocity);

        // Rotate the velocity vector
        // vel = pvel => Transfer all (full grip)
        if (isRotating) {
            vel = vel * (1 - rotVel) + pvel * rotVel; // Partial transfer
            //vel = vel.normalized * speed;
        }

        // Sideway grip
        isRight = vel.x > 0f ? 1f : -1f;
        vel.x -= isRight * gripX * Time.deltaTime;  // Accelerate in opposing direction
        if (vel.x * isRight < 0f) vel.x = 0f;       // Check if changed polarity

        // Straight grip
        isForward = vel.z > 0f ? 1f : -1f;
        vel.z -= isForward * gripZ * Time.deltaTime;
        if (vel.z * isForward < 0f) vel.z = 0f;

        // Top speed
        if (vel.z > TopSpeed) vel.z = TopSpeed;
        else if (vel.z < -TopSpeed) vel.z = -TopSpeed;

        rigidBody.velocity = transform.TransformDirection(vel);
        #endregion

    }



    #region Controllers
    // Get input values from keyboard
    void InputKeyboard() {
        inThrottle = Input.GetAxisRaw("Throttle");
        inBoost = Input.GetAxisRaw("Boost") > 0f;
        inTurn = Input.GetAxisRaw("Sideways");

        // Reset will turn false after the respawn is successful
        inReset = inReset || Input.GetKeyDown(KeyCode.R);
    }

    void InputEnemy() {
        inThrottle = 1f;

        // Turn by facing player
        // Get the angle between the points (absolute goal) = right (target) - left
        float angle = AngleOffset(Angle2Points(transform.position, Target.position), 0f);

        Vector3 rot = transform.eulerAngles;
        float delta = Mathf.DeltaAngle(rot.y, angle);
        //inTurn = delta > 0f ? 1f : -1f;

        if (delta > 10f) inTurn = 1f;
        else if (delta < -10f) inTurn = -1f;
        else inTurn = 0f;
    }

    // Executing the queued inputs
    void Controller() {

        if (inBoost) accel *= Boost; // Higher acceleration

        if (inThrottle > 0.5f || inThrottle < -0.5f) {
            rigidBody.velocity += transform.forward * inThrottle * accel * Time.deltaTime;
            gripZ = 0f;     // Remove straight grip if wheel is rotating
        }

        if (autoReset) {
            // If stuck, check next frame too then reset
            if (pvel.magnitude <= 0.01f) {
                inReset = isStuck;  // So, true on next frame
                isStuck = true;
            } else {
                isStuck = false;
            }
        }

        if (inReset) {  // Reset
            float y = transform.eulerAngles.y;
            transform.eulerAngles = new Vector3(0, y, 0);
            rigidBody.velocity = new Vector3(0, -1f, 0);
            transform.position += Vector3.up * 2;
            inReset = false;
        }

        isRotating = false;

        // Get the local-axis velocity before new input (+x, +y, and +z = right, up, and forward)
        pvel = transform.InverseTransformDirection(rigidBody.velocity);

        // Turn statically
        if (inTurn > 0.5f || inTurn < -0.5f) {
            float dir = (pvel.z < 0) ? -1 : 1;    // To fix direction on reverse
            RotateGradConst(inTurn * dir);
        }
    }
    #endregion



    #region Rotation Methods
    /* Advised to not read eulerAngles, only write: https://answers.unity.com/questions/462073/
     * As it turns out, the problem isn't there. */

    /* As is: Conflict with physical Y-axis rotation, must be disabled.
     * Current methods:
     * 1. Prevent rotational input when there's angular velocity.
     * 2. Significantly increase angular drag while grounded.
     * 3. Result: rotation responding to environment, responsive input, & natural stumbling.
     */

    Vector3 drot = new Vector3(0f, 0f, 0f);

    void RotateInstant(float angle) {
        if (rotate > 0f) {
            Vector3 rot = transform.eulerAngles;
            rot.y = angle;
            transform.eulerAngles = rot;
            isRotating = true;
        }
    }

    void RotateGradConst(float isCW) {
        // Delta = right(taget) - left(current)
        drot.y = isCW * rotate * Time.deltaTime;
        transform.rotation *= Quaternion.AngleAxis(drot.y, transform.up);
        isRotating = true;
    }

    void RotateGradAbsolute(float angle) {
        // Delta = right(taget) - left(current)
        Vector3 rot = transform.eulerAngles;
        rot.y = AngleOffset(rot.y, 0f);

        float delta = Mathf.DeltaAngle(rot.y, angle);
        float isCW = delta > 0f ? 1f : -1f;
        rot.y += isCW * rotate * Time.deltaTime;
        rot.y = AngleOffset(rot.y, 0f);

        delta = Mathf.DeltaAngle(AngleOffset(rot.y, 0f), angle);
        if (delta * isCW < 0f) rot.y = angle;       // Check if changed polarity
        else isRotating = true;

        // You can't set them directly as it'll set x & z to zero
        // if you're not using eulerAngles.x & z.
        transform.eulerAngles = rot;
        //transform.rotation = Quaternion.AngleAxis(rot.y, Vector3.up);
        //rigidBody.MoveRotation(Quaternion.Euler(rot));
    }

    void RotateGradRelative(float angle) {
        // Delta = right(taget) - left(current)
        Vector3 rot = transform.eulerAngles;
        rot.y = AngleOffset(rot.y, 0f);

        float delta = Mathf.DeltaAngle(rot.y, angle);
        float isCW = delta > 0f ? 1f : -1f;

        // Value add to transform.eulerAngles
        drot.y = isCW * rotate * Time.deltaTime;
        drot.y = AngleOffset(rot.y, 0f);

        delta = Mathf.DeltaAngle(AngleOffset(rot.y, drot.y), angle);
        if (delta * isCW < 0f) drot.y = 0;       // Check if changed polarity
        else isRotating = true;

        // Add the drot to current rotation
        transform.rotation *= Quaternion.AngleAxis(drot.y, transform.up);
        //rigidBody.rotation *= Quaternion.AngleAxis(drot.y, transform.up);
        //transform.Rotate(drot, Space.Self);
        //rigidBody.AddTorque(drot);
        //rigidBody.MoveRotation(rigidBody.rotation * Quaternion.Euler(drot));
    }
    #endregion



    #region Utilities
    void DebugPlayer(object message) {
        if (carFaction == Faction.Player) Debug.Log(message);
    }

    float Angle2Points(Vector3 a, Vector3 b) {
        //return Mathf.Atan2(b.y - a.y, b.x - a.x) * Mathf.Rad2Deg;
        return Mathf.Atan2(b.x - a.x, b.z - a.z) * Mathf.Rad2Deg;
    }

    float AngleOffset(float raw, float offset) {
        raw = (raw + offset) % 360;             // Mod by 360, to not exceed 360
        if (raw > 180.0f) raw -= 360.0f;
        if (raw < -180.0f) raw += 360.0f;
        return raw;
    }

    // Get bound of a large 
    public static Bounds GetBounds(GameObject obj) {

        // Switch every collider to renderer for more accurate result
        Bounds bounds = new Bounds();
        Collider[] colliders = obj.GetComponentsInChildren<Collider>();

        if (colliders.Length > 0) {

            //Find first enabled renderer to start encapsulate from it
            foreach (Collider collider in colliders) {

                if (collider.enabled) {
                    bounds = collider.bounds;
                    break;
                }
            }

            //Encapsulate (grow bounds to include another) for all collider
            foreach (Collider collider in colliders) {
                if (collider.enabled) {
                    bounds.Encapsulate(collider.bounds);
                }
            }
        }
        return bounds;
    }
    #endregion
}
