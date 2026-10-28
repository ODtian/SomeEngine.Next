using System.Numerics;
using System.Text.Json.Nodes;
using SomeEngine.ECS.Entities;
using SomeEngine.Physics;

namespace DefaultGame;

/// <summary>Original scale-scene groups, current completed motion and compact event-boundary aggregates.</summary>
internal sealed class ScaleMotionMeasurements
{
    private readonly Dictionary<Entity, int> _bodyIndices;
    private readonly Dictionary<int, int> _impactorStructures = [];
    private readonly int[] _bodyGroups;
    private readonly Group[] _groups;
    private readonly bool[] _hitStructures, _loadedStructures;
    private readonly HashSet<long> _checkpointSteps;
    private readonly Dictionary<int, InputMeasurement> _inputs;
    private readonly List<JsonObject> _checkpoints = [];
    private readonly int[] _eventCounts = new int[15];
    private readonly double[] _eventMinimumTime = new double[15], _eventMaximumTime = new double[15];
    private readonly int[] _eventMinimumSubstep = new int[15], _eventMaximumSubstep = new int[15];
    private readonly bool _million, _breakable;
    private readonly int _initialJoints, _dynamicBodies, _substeps, _inputStep, _sharedFloorStep, _columns, _bodiesPerLayer, _lastStep;
    private readonly float _stepSeconds;
    private readonly ConstraintBreakSettings? _breakLimit;
    private long _step = -1, _eventStep = -1, _breaks, _sleeps, _wakes, _localWakes, _sharedFloorWakes;
    private long? _firstBreakStep, _lastBreakStep;
    private Entity _lastBrokenJoint;
    private Entity _lastContactPoint;
    private int _lastBreakBody, _lastBreakFirst, _lastBreakSecond;
    private int _readBodies, _clockBodies, _liveJoints, _liveSelfContacts, _stepSelfContacts;
    private int _previousBreaks, _completedBreaks;
    private int? _islands, _previousIslands, _graphActiveBodies;
    private int _wakeMinColumn, _wakeMaxColumn, _wakeMinRow, _wakeMaxRow, _wakeMinLayer, _wakeMaxLayer;
    private double _endTime;
    private bool _deliverySatisfied = true, _breakForcesSatisfied = true, _eventWindowsSatisfied = true;

    internal ScaleMotionMeasurements(PhysicsSceneDescription description, PhysicsSceneData data,
        ReadOnlySpan<Entity> bodies, int stepCount)
    {
        _bodyIndices = new(bodies.Length);
        _million = description.Scene == 407;
        bool pyramids = description.Scene is 402 or 403, network = description.Scene == 404;
        _initialJoints = data.Joints.Length; _substeps = data.Substeps;
        _lastStep = stepCount - 1; _stepSeconds = (float)data.StepSeconds;
        _inputs = data.Events.ToDictionary(change => change.Body!.Value, change => new InputMeasurement(change));
        _breakLimit = data.Joints.FirstOrDefault(joint => joint.BreakLimits.HasValue)?.BreakLimits;
        _breakable = _breakLimit.HasValue;
        _inputStep = data.Events.Where(change => change.Velocity is not null).Min(change => change.Frame);
        _sharedFloorStep = _million ? data.Events.Single(change => change.Position is not null).Frame : int.MaxValue;
        _checkpointSteps = data.Events.SelectMany(change => new long[] { change.Frame - 1, change.Frame })
            .Append(stepCount - 1L).ToHashSet();
        _bodyGroups = new int[bodies.Length];
        var groups = new List<Group> { new("Static references", false, -1) };
        int structureCount = network || _million ? 0 : pyramids ? data.Events.Length : 1;
        _hitStructures = new bool[structureCount]; _loadedStructures = new bool[structureCount];
        if (pyramids)
        {
            for (int index = 0; index < structureCount; index++) groups.Add(new($"Pyramid {index}", true, index));
            for (int index = 0; index < data.Events.Length; index++)
                _impactorStructures.Add(data.Events[index].Body!.Value, index);
            int bricksPerStructure = (bodies.Length - 1 - structureCount) / structureCount;
            int impactors = groups.Count; groups.Add(new("Impactors", true, -1));
            for (int index = 1; index < bodies.Length; index++)
                _bodyGroups[index] = _impactorStructures.ContainsKey(index) ? impactors : 1 + (index - 1) / bricksPerStructure;
        }
        else if (_million)
        {
            double firstHeight = data.Bodies[1].Position[2];
            _bodiesPerLayer = data.Bodies.Count(body => body.Mass > 0 && body.Position[2] == firstHeight);
            _columns = data.Bodies.Where(body => body.Mass > 0).Select(body => body.Position[0]).Distinct().Count();
            int target = data.Events.Single(change => change.Velocity is not null).Body!.Value;
            int targetColumn = (target - 1) % _bodiesPerLayer;
            int layers = (bodies.Length - 1) / _bodiesPerLayer;
            groups.Add(new("Impulse column", true, -1));
            for (int layer = 0; layer < layers; layer++) groups.Add(new($"Other columns, layer {layer}", true, -1));
            for (int index = 1; index < bodies.Length; index++)
                _bodyGroups[index] = (index - 1) % _bodiesPerLayer == targetColumn ? 1 : 2 + (index - 1) / _bodiesPerLayer;
        }
        else if (network)
        {
            groups.Add(new("Hanging nodes", true, -1)); groups.Add(new("Impulse node", true, -1));
            int target = data.Events.Single().Body!.Value;
            for (int index = 0; index < bodies.Length; index++)
                _bodyGroups[index] = data.Bodies[index].Mass == 0 ? 0 : index == target ? 2 : 1;
        }
        else
        {
            foreach (SceneInputEvent change in data.Events) _impactorStructures.Add(change.Body!.Value, 0);
            var layers = new Dictionary<double, int>();
            for (int index = 0; index < bodies.Length; index++)
            {
                SceneBodyData body = data.Bodies[index];
                if (body.Mass == 0 || _impactorStructures.ContainsKey(index)) continue;
                double height = body.Position[2];
                if (!layers.TryGetValue(height, out int group))
                { group = groups.Count; layers.Add(height, group); groups.Add(new($"Brick layer {layers.Count - 1}", true, 0)); }
                _bodyGroups[index] = group;
            }
            int impactors = groups.Count; groups.Add(new("Impactors", true, -1));
            foreach (int index in _impactorStructures.Keys) _bodyGroups[index] = impactors;
        }
        _groups = groups.ToArray();
        for (int index = 0; index < bodies.Length; index++)
        {
            _bodyIndices.Add(bodies[index], index);
            Group group = _groups[_bodyGroups[index]];
            group.Bodies++; if (group.Dynamic) _dynamicBodies++;
            group.InitialX += data.Bodies[index].Position[0]; group.InitialY += data.Bodies[index].Position[1];
            group.InitialZ += data.Bodies[index].Position[2];
        }
        foreach (Group group in _groups)
        { group.InitialX /= group.Bodies; group.InitialY /= group.Bodies; group.InitialZ /= group.Bodies; }
    }

    internal void ReadEvent(in PhysicsStepEvents step, in PhysicsEvent value)
    {
        _eventWindowsSatisfied &= step.Number == _step + 1 && value.Substep >= 0 && value.Substep < _substeps
            && value.Time >= step.StartTime && value.Time <= step.EndTime;
        if (_eventStep != step.Number)
        {
            _eventStep = step.Number; Array.Clear(_eventCounts); _stepSelfContacts = 0;
            _wakeMinColumn = _wakeMinRow = _wakeMinLayer = int.MaxValue;
            _wakeMaxColumn = _wakeMaxRow = _wakeMaxLayer = int.MinValue;
            foreach (Group group in _groups) group.ClearEvents();
        }
        int eventIndex = (int)value.Kind * 3 + (int)value.Phase;
        if (_eventCounts[eventIndex]++ == 0)
        {
            _eventMinimumTime[eventIndex] = _eventMaximumTime[eventIndex] = value.Time;
            _eventMinimumSubstep[eventIndex] = _eventMaximumSubstep[eventIndex] = value.Substep;
        }
        else
        {
            _eventMinimumTime[eventIndex] = Math.Min(_eventMinimumTime[eventIndex], value.Time);
            _eventMaximumTime[eventIndex] = Math.Max(_eventMaximumTime[eventIndex], value.Time);
            _eventMinimumSubstep[eventIndex] = Math.Min(_eventMinimumSubstep[eventIndex], value.Substep);
            _eventMaximumSubstep[eventIndex] = Math.Max(_eventMaximumSubstep[eventIndex], value.Substep);
        }
        if (value.Kind is PhysicsEventKind.Sleep or PhysicsEventKind.Wake)
        {
            int index = _bodyIndices[value.Subject]; Group group = _groups[_bodyGroups[index]];
            if (value.Kind == PhysicsEventKind.Sleep) { group.SleepEvents++; _sleeps++; }
            else
            {
                group.WakeEvents++; _wakes++;
                if (_million)
                {
                    if (step.Number >= _sharedFloorStep) _sharedFloorWakes++;
                    else if (step.Number >= _inputStep) _localWakes++;
                    int cell = (index - 1) % _bodiesPerLayer, column = cell % _columns, row = cell / _columns;
                    int layer = (index - 1) / _bodiesPerLayer;
                    _wakeMinColumn = Math.Min(_wakeMinColumn, column); _wakeMaxColumn = Math.Max(_wakeMaxColumn, column);
                    _wakeMinRow = Math.Min(_wakeMinRow, row); _wakeMaxRow = Math.Max(_wakeMaxRow, row);
                    _wakeMinLayer = Math.Min(_wakeMinLayer, layer); _wakeMaxLayer = Math.Max(_wakeMaxLayer, layer);
                }
            }
            return;
        }
        if (value.Kind is not (PhysicsEventKind.Contact or PhysicsEventKind.Break)) return;
        int first = _bodyIndices[value.First], second = _bodyIndices[value.Second];
        Group a = _groups[_bodyGroups[first]], b = _groups[_bodyGroups[second]];
        if (value.Kind == PhysicsEventKind.Break)
        {
            _breaks++; _firstBreakStep ??= step.Number; _lastBreakStep = step.Number; _lastBrokenJoint = value.Subject;
            _lastBreakBody = _bodyIndices[value.Body]; _lastBreakFirst = first; _lastBreakSecond = second;
            _groups[_bodyGroups[_lastBreakBody]].ReportedBreakEvents++;
            a.BreakEvents++; if (!ReferenceEquals(a, b)) b.BreakEvents++;
            float force = value.Force.Length(), torque = value.Torque.Length();
            a.BreakForce = MathF.Max(a.BreakForce, force); b.BreakForce = MathF.Max(b.BreakForce, force);
            a.BreakTorque = MathF.Max(a.BreakTorque, torque); b.BreakTorque = MathF.Max(b.BreakTorque, torque);
            if (_breakLimit is { } limit)
                _breakForcesSatisfied &= value.Force.LengthSquared() > limit.MaximumForce * limit.MaximumForce
                    || value.Torque.LengthSquared() > limit.MaximumTorque * limit.MaximumTorque;
            return;
        }
        _lastContactPoint = value.Subject;
        a.ContactEvents++; if (!ReferenceEquals(a, b)) b.ContactEvents++;
        if (a.Dynamic && b.Dynamic)
        {
            if (value.Phase == PhysicsEventPhase.Begin) _liveSelfContacts++;
            else if (value.Phase == PhysicsEventPhase.End) _liveSelfContacts--;
            if (value.Phase != PhysicsEventPhase.End) _stepSelfContacts++;
        }
        if (value.Phase == PhysicsEventPhase.End || step.Number < _inputStep) return;
        int structure;
        if (_impactorStructures.TryGetValue(first, out structure) && b.Structure == structure
            || _impactorStructures.TryGetValue(second, out structure) && a.Structure == structure)
        {
            _hitStructures[structure] = true;
            if (Vector3.Dot(value.Force, value.Normal) > 0) _loadedStructures[structure] = true;
        }
    }

    internal void BeginRead(in PhysicsStepResult completed)
    {
        _deliverySatisfied &= completed.Number == _step + 1;
        _step = completed.Number; _endTime = completed.EndTime; _readBodies = _clockBodies = 0;
        foreach (Group group in _groups) group.ClearMotion();
    }

    internal void ReadBody(Entity entity, in BodyPose pose, in BodyMass mass, in BodyVelocity velocity,
        in BodyActivity activity, in BodyStepState step)
    {
        int bodyIndex = _bodyIndices[entity];
        Group group = _groups[_bodyGroups[bodyIndex]];
        Vector3 center = pose.GetCenterOfMass(in mass);
        if (_inputs.TryGetValue(bodyIndex, out InputMeasurement? input))
            input.Read(_step, _stepSeconds, in pose, in velocity, in step);
        _readBodies++; group.X += center.X; group.Y += center.Y; group.Z += center.Z;
        group.MinimumZ = MathF.Min(group.MinimumZ, center.Z); group.MaximumZ = MathF.Max(group.MaximumZ, center.Z);
        group.Speed = MathF.Max(group.Speed, velocity.Linear.Length()); group.AngularSpeed = MathF.Max(group.AngularSpeed, velocity.Angular.Length());
        if (mass.Motion != BodyMotion.Dynamic) return;
        if (activity.Sleeping) group.Sleeping++; else group.Awake++;
        group.QuietSeconds = MathF.Max(group.QuietSeconds, activity.QuietSeconds);
        if (step.Number == _step && step.Substep == _substeps - 1 && step.Time == _endTime) _clockBodies++;
    }

    internal void FinishRead(int liveJoints, DeviceSceneStatistics? statistics)
    {
        _liveJoints = liveJoints; _previousIslands = _islands;
        _islands = statistics?.IslandCount; _graphActiveBodies = statistics?.GraphActiveBodies;
        _previousBreaks = _completedBreaks;
        _completedBreaks = _eventStep == _step ? _eventCounts[(int)PhysicsEventKind.Break * 3] : 0;
        _deliverySatisfied &= _readBodies == _bodyGroups.Length && _clockBodies == _dynamicBodies
            && (!_breakable || _liveJoints + _breaks == _initialJoints) && _breakForcesSatisfied && _eventWindowsSatisfied;
        if (_checkpointSteps.Contains(_step)) _checkpoints.Add(DescribeCurrent());
    }

    internal bool DeliverySatisfied => _deliverySatisfied && _step == _lastStep;
    internal bool InputDeliverySatisfied => _step == _lastStep && _inputs.Values.All(input => input.Delivered);

    internal JsonObject Describe()
    {
        JsonObject result = DescribeCurrent();
        result["Checkpoints"] = new JsonArray(_checkpoints.Select(value => value.DeepClone()).ToArray());
        return result;
    }

    private JsonObject DescribeCurrent()
    {
        bool currentEvents = _eventStep == _step;
        int wakeEvents = currentEvents ? _eventCounts[(int)PhysicsEventKind.Wake * 3] : 0;
        return new JsonObject
        {
            ["Scope"] = "Every original scene body; current event span and group aggregates at original input boundaries",
            ["Step"] = _step, ["Time"] = _endTime, ["DeliveredBodies"] = _readBodies,
            ["DynamicBodies"] = _dynamicBodies, ["DynamicBodiesAtCompletedClock"] = _clockBodies,
            ["DeliverySatisfied"] = _deliverySatisfied,
            ["OriginalLastStep"] = _lastStep, ["OriginalProcessCompleted"] = _step == _lastStep,
            ["EventsWithinCompletedStep"] = _eventWindowsSatisfied,
            ["InputDeliverySatisfied"] = InputDeliverySatisfied,
            ["Inputs"] = new JsonArray(_inputs.Where(pair => pair.Value.Step == _step || _step == _lastStep)
                .Select(pair => (JsonNode)pair.Value.Describe(pair.Key)).ToArray()),
            ["GraphSourceStep"] = _islands.HasValue ? _step : null,
            ["GraphStage"] = _islands.HasValue ? "Connectivity and active work before solver and automatic joint breaks" : null,
            ["GraphActiveBodies"] = _graphActiveBodies, ["DynamicIslands"] = _islands,
            ["PreviousGraphDynamicIslands"] = _previousIslands, ["PreviousStepBreakEvents"] = _previousBreaks,
            ["LiveSelfContactPoints"] = _liveSelfContacts,
            ["SelfContactPointEventsThisStep"] = currentEvents ? _stepSelfContacts : 0,
            ["BreakEvents"] = _breaks, ["LiveJoints"] = _liveJoints, ["InitialJoints"] = _initialJoints,
            ["BreakForcesAboveDeclaredLimits"] = _breakable ? _breakForcesSatisfied : null,
            ["FirstBreakStep"] = _firstBreakStep, ["LastBreakStep"] = _lastBreakStep,
            ["LastBrokenJoint"] = _lastBreakStep.HasValue ? _lastBrokenJoint.ToString() : null,
            ["LastBreakReportingBodyIndex"] = _lastBreakStep.HasValue ? _lastBreakBody : null,
            ["LastBreakFirstBodyIndex"] = _lastBreakStep.HasValue ? _lastBreakFirst : null,
            ["LastBreakSecondBodyIndex"] = _lastBreakStep.HasValue ? _lastBreakSecond : null,
            ["LastContactPoint"] = _lastContactPoint == Entity.Null ? null : _lastContactPoint.ToString(),
            ["SleepEvents"] = _sleeps, ["WakeEvents"] = _wakes,
            ["WakeEventsAfterLocalInputBeforeFloorMove"] = _million ? _localWakes : null,
            ["WakeEventsAfterSharedFloorMove"] = _million ? _sharedFloorWakes : null,
            ["WakeInputGridMinimum"] = _million && wakeEvents > 0 ? new JsonArray(_wakeMinColumn, _wakeMinRow, _wakeMinLayer) : null,
            ["WakeInputGridMaximum"] = _million && wakeEvents > 0 ? new JsonArray(_wakeMaxColumn, _wakeMaxRow, _wakeMaxLayer) : null,
            ["StructuresWithImpactorContacts"] = _hitStructures.Count(value => value),
            ["StructuresWithLoadedImpactorContacts"] = _loadedStructures.Count(value => value),
            ["Structures"] = _hitStructures.Length,
            ["StepEvents"] = new JsonArray(Enumerable.Range(0, 15).Where(index => currentEvents && _eventCounts[index] != 0)
                .Select(index => (JsonNode)new JsonObject { ["Kind"] = ((PhysicsEventKind)(index / 3)).ToString(),
                    ["Phase"] = ((PhysicsEventPhase)(index % 3)).ToString(), ["Count"] = _eventCounts[index],
                    ["MinimumTime"] = _eventMinimumTime[index], ["MaximumTime"] = _eventMaximumTime[index],
                    ["MinimumSubstep"] = _eventMinimumSubstep[index], ["MaximumSubstep"] = _eventMaximumSubstep[index] }).ToArray()),
            ["Groups"] = new JsonArray(_groups.Select(group => (JsonNode)group.Describe(currentEvents)).ToArray()),
        };
    }

    private sealed class InputMeasurement(SceneInputEvent input)
    {
        private Vector3 _previousVelocity;
        private double? _velocityIncrement, _roundingTolerance;
        private Vector3? _position;
        internal int Step => input.Frame;
        internal bool Delivered { get; private set; }

        internal void Read(long number, float duration, in BodyPose pose, in BodyVelocity velocity, in BodyStepState step)
        {
            if (number == input.Frame)
            {
                if (input.Velocity is { } increment)
                {
                    // All original scale impulses act on X or Y, perpendicular to gravity.
                    int axis = increment[0] != 0 ? 0 : 1;
                    double start = step.StartCenterOfMass[axis], predicted = step.PredictedCenterOfMass[axis];
                    double previous = _previousVelocity[axis], requested = increment[axis];
                    _velocityIncrement = (predicted - start) / duration - previous;
                    // Float prediction rounds velocity arithmetic, displacement and position addition.
                    const double unitRoundoff = 1d / (1 << 24);
                    _roundingTolerance = 8 * unitRoundoff *
                        ((Math.Abs(start) + Math.Abs(predicted)) / duration + Math.Abs(previous) + Math.Abs(requested));
                    Delivered = Math.Abs(_velocityIncrement.Value - requested) <= _roundingTolerance.Value;
                }
                else if (input.Position is { } position)
                {
                    _position = pose.Position;
                    Delivered = pose.Position == new Vector3((float)position[0], (float)position[1], (float)position[2]);
                }
            }
            _previousVelocity = velocity.Linear;
        }

        internal JsonObject Describe(int bodyIndex) => new()
        {
            ["Step"] = input.Frame, ["BodyIndex"] = bodyIndex, ["Delivered"] = Delivered,
            ["RequestedVelocityIncrement"] = input.Velocity is { } velocity
                ? new JsonArray(velocity[0], velocity[1], velocity[2]) : null,
            ["PredictedVelocityIncrementAlongInputAxis"] = _velocityIncrement,
            ["FloatRoundingTolerance"] = _roundingTolerance,
            ["RequestedPosition"] = input.Position is { } position
                ? new JsonArray(position[0], position[1], position[2]) : null,
            ["CompletedPosition"] = _position is { } completed ? new JsonArray(completed.X, completed.Y, completed.Z) : null,
        };
    }

    private sealed class Group(string name, bool dynamic, int structure)
    {
        internal readonly bool Dynamic = dynamic;
        internal readonly int Structure = structure;
        internal int Bodies, Awake, Sleeping, ContactEvents, BreakEvents, ReportedBreakEvents, SleepEvents, WakeEvents;
        internal double InitialX, InitialY, InitialZ, X, Y, Z;
        internal float MinimumZ, MaximumZ, Speed, AngularSpeed, QuietSeconds, BreakForce, BreakTorque;
        internal void ClearEvents() { ContactEvents = BreakEvents = ReportedBreakEvents = SleepEvents = WakeEvents = 0; BreakForce = BreakTorque = 0; }
        internal void ClearMotion()
        { Awake = Sleeping = 0; X = Y = Z = 0; MinimumZ = float.PositiveInfinity; MaximumZ = float.NegativeInfinity; Speed = AngularSpeed = QuietSeconds = 0; }
        internal JsonObject Describe(bool events) => new()
        {
            ["Name"] = name, ["Bodies"] = Bodies, ["AwakeBodies"] = Awake, ["SleepingBodies"] = Sleeping,
            ["InitialMeanCenter"] = new JsonArray(InitialX, InitialY, InitialZ),
            ["MeanCenter"] = new JsonArray(X / Bodies, Y / Bodies, Z / Bodies),
            ["MeanCenterDisplacement"] = new JsonArray(X / Bodies - InitialX, Y / Bodies - InitialY, Z / Bodies - InitialZ),
            ["MinimumCenterZ"] = MinimumZ, ["MaximumCenterZ"] = MaximumZ,
            ["MaximumSpeed"] = Speed, ["MaximumAngularSpeed"] = AngularSpeed, ["MaximumQuietSeconds"] = QuietSeconds,
            ["ContactPointEventsThisStep"] = events ? ContactEvents : 0, ["BreakEventsThisStep"] = events ? BreakEvents : 0,
            ["ReportingBodyBreakEventsThisStep"] = events ? ReportedBreakEvents : 0,
            ["SleepEventsThisStep"] = events ? SleepEvents : 0, ["WakeEventsThisStep"] = events ? WakeEvents : 0,
            ["MaximumBreakForceThisStep"] = events ? BreakForce : 0, ["MaximumBreakTorqueThisStep"] = events ? BreakTorque : 0,
        };
    }
}
