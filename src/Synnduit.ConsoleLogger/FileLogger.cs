using Serilog;
using Synnduit.Events;
using System.ComponentModel.Composition;

namespace Synnduit.Logging
{
    /// <summary>
    /// Logs individual run events to the configured Serilog sink(s) as plain, one-line-per-
    /// event messages. Runs in parallel with the <see cref="ConsoleLogger{TEntity}" />: the
    /// console logger keeps the rich, in-place-updating console view, while this receiver
    /// produces a clean, greppable record suitable for the Serilog File sink.
    /// </summary>
    /// <remarks>
    /// This receiver writes through the static <see cref="Log" /> logger, which the host
    /// application (Synnduit) configures from <c>appsettings.json</c> via
    /// <c>ReadFrom.Configuration</c>. No additional wiring is required: because this type is
    /// decorated with <see cref="EventReceiverAttribute" /> and lives in the same assembly
    /// as <see cref="ConsoleLogger{TEntity}" /> (which the host already loads into the
    /// runner), MEF discovers it automatically and the event dispatcher delivers every event
    /// to it alongside the console logger.
    /// </remarks>
    /// <typeparam name="TEntity">The type representing the entity.</typeparam>
    [EventReceiver]
    public class FileLogger<TEntity> : EventReceiver<TEntity>
        where TEntity : class
    {
        private readonly Dictionary<EntityTransactionOutcome, int> results;

        private DateTime segmentStartTime;

        private int entityCount;

        private int entitiesDeleted;

        /// <summary>
        /// Initializes a new instance of the class.
        /// </summary>
        [ImportingConstructor]
        public FileLogger()
        {
            this.results = CreateResults();
            this.entityCount = 0;
            this.entitiesDeleted = 0;
        }

        /// <summary>
        /// Called when the current run segment is about to be executed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnSegmentExecuting(ISegmentExecutingArgs args)
        {
            this.segmentStartTime = DateTime.Now;
            Log.Information(
                "Segment {SegmentIndex}/{SegmentCount} starting — {SegmentType} of " +
                "{EntityType}; source: {SourceSystem}, destination: {DestinationSystem}",
                this.Context.SegmentIndex,
                this.Context.SegmentCount,
                this.Context.SegmentType,
                this.Context.EntityType.Name,
                this.Context.SourceSystem?.Name ?? "(none)",
                this.Context.DestinationSystem.Name);
        }

        /// <summary>
        /// Called when the current run segment finishes executing.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnSegmentExecuted(ISegmentExecutedArgs args)
        {
            double durationSeconds =
                (DateTime.Now - this.segmentStartTime).TotalSeconds;
            if(this.Context.SegmentType == SegmentType.Migration)
            {
                Log.Information(
                    "Segment {SegmentIndex}/{SegmentCount} completed in " +
                    "{DurationSeconds:F1}s — {EntityType}: {Results}" + Environment.NewLine,
                    this.Context.SegmentIndex,
                    this.Context.SegmentCount,
                    durationSeconds,
                    this.Context.EntityType.Name,
                    this.GetResultsSummary());
            }
            else if(this.Context.SegmentType == SegmentType.GarbageCollection)
            {
                Log.Information(
                    "Segment {SegmentIndex}/{SegmentCount} completed in " +
                    "{DurationSeconds:F1}s — {EntityType}: {EntitiesDeleted:#,##0} of " +
                    "{EntityCount:#,##0} entities deleted" + Environment.NewLine,
                    this.Context.SegmentIndex,
                    this.Context.SegmentCount,
                    durationSeconds,
                    this.Context.EntityType.Name,
                    this.entitiesDeleted,
                    this.entityCount);
            }
            else
            {
                Log.Information(
                    "Segment {SegmentIndex}/{SegmentCount} completed in " +
                    "{DurationSeconds:F1}s — {EntityType}" + Environment.NewLine,
                    this.Context.SegmentIndex,
                    this.Context.SegmentCount,
                    durationSeconds,
                    this.Context.EntityType.Name);
            }
        }

        /// <summary>
        /// Called when a subsystem is about to be initialized.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnInitializing(IInitializingArgs args)
        {
            if(args.Message != null)
            {
                Log.Information("{Message}", args.Message);
            }
        }

        /// <summary>
        /// Called when a subsystem has been initialized.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnInitialized(IInitializedArgs args)
        {
            if(args.Message != null)
            {
                Log.Information("{Message}", args.Message);
            }
        }

        /// <summary>
        /// Called when source/destination system identifier mappings have been loaded from
        /// the database into the in-memory cache.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnMappingsCached(IMappingsCachedArgs args)
        {
            Log.Information(
                "Loaded {Count:#,##0} entity identifier mapping(s)", args.Count);
        }

        /// <summary>
        /// Called when the (deduplication) in-memory cache of destination system entities
        /// has been populated.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnCachePopulated(ICachePopulatedArgs args)
        {
            Log.Information(
                "Cached {Count:#,##0} destination system entity(ies) for deduplication",
                args.Count);
        }

        /// <summary>
        /// Called when entities from the source system have been loaded.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnLoaded(ILoadedArgs args)
        {
            this.entityCount = args.Count;
            Log.Information(
                "Loaded {Count:#,##0} entity(ies) from the source system", args.Count);
        }

        /// <summary>
        /// Called when a source system entity has been processed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnProcessed(IProcessedArgs<TEntity> args)
        {
            this.results[args.Outcome]++;
            switch(args.Outcome)
            {
                case EntityTransactionOutcome.ExceptionThrown:
                    Log.Error(
                        "Exception thrown while processing {EntityType} entity " +
                        "{SourceSystemEntityId}",
                        this.Context.EntityType.Name,
                        this.DescribeSourceId(args));
                    break;
                case EntityTransactionOutcome.ReferredForManualDeduplication:
                    Log.Warning(
                        "{EntityType} entity {SourceSystemEntityId} referred for manual " +
                        "deduplication",
                        this.Context.EntityType.Name,
                        this.DescribeSourceId(args));
                    break;
            }
        }

        /// <summary>
        /// Called when a garbage collection run segment has been initialized.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnGarbageCollectionInitialized(
            IGarbageCollectionInitializedArgs args)
        {
            this.entityCount = args.Count;
            this.entitiesDeleted = 0;
            Log.Information(
                "Identified {Count:#,##0} entity(ies) for deletion", args.Count);
        }

        /// <summary>
        /// Called when the deletion of a destination system entity (identified for
        /// deletion) has been processed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnDeletionProcessed(IDeletionProcessedArgs args)
        {
            this.entitiesDeleted++;
        }

        /// <summary>
        /// Called when orphan identifier mappings are about to be processed.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnOrphanMappingsProcessing(IOrphanMappingsProcessingArgs args)
        {
            Log.Information(
                "Processing {Count:#,##0} orphan mapping(s); behavior: {Behavior}",
                args.Count,
                args.Behavior);
        }

        /// <summary>
        /// Called when the processing of orphan mappings has been aborted.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnOrphanMappingsProcessingAborted(
            IOrphanMappingsProcessingAbortedArgs args)
        {
            Log.Error(
                "Orphan mapping processing aborted ({Scope}); orphaned percentage " +
                "{Percentage:F2}% exceeded the threshold of {Threshold:F2}%" +
                Environment.NewLine,
                args.RunAborted ? "run" : "segment",
                args.Percentage * 100.0d,
                args.Threshold * 100.0d);
        }

        /// <summary>
        /// Called when a garbage collection run segment has been aborted.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnGarbageCollectionAborted(IGarbageCollectionAbortedArgs args)
        {
            Log.Error(
                "Garbage collection aborted ({Scope}); deletion percentage " +
                "{Percentage:F2}% exceeded the threshold of {Threshold:F2}%" +
                Environment.NewLine,
                args.RunAborted ? "run" : "segment",
                args.Percentage * 100.0d,
                args.Threshold * 100.0d);
        }

        /// <summary>
        /// Called when a run segment has been aborted.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnSegmentAborted(ISegmentAbortedArgs args)
        {
            Log.Error(
                "Segment {SegmentIndex}/{SegmentCount} aborted — {EntityType}: the number " +
                "of exceptions reached the segment abort threshold of {Threshold}. " +
                "Results so far: {Results}" + Environment.NewLine,
                this.Context.SegmentIndex,
                this.Context.SegmentCount,
                this.Context.EntityType.Name,
                args.Threshold,
                this.GetResultsSummary());
        }

        /// <summary>
        /// Called when the run has been aborted.
        /// </summary>
        /// <param name="args">The event data.</param>
        public override void OnRunAborted(IRunAbortedArgs args)
        {
            Log.Error(
                "Run aborted — {EntityType}: the number of exceptions reached the run " +
                "abort threshold of {Threshold}. Results so far: {Results}" +
                Environment.NewLine,
                this.Context.EntityType.Name,
                args.Threshold,
                this.GetResultsSummary());
        }

        private string DescribeSourceId(IProcessedArgs<TEntity> args)
        {
            return args.SourceSystemEntityId?.ToString() ?? "(unknown)";
        }

        private string GetResultsSummary()
        {
            IEnumerable<string> nonZeroOutcomes =
                this.results
                .Where(result => result.Value > 0)
                .OrderBy(result => (int) result.Key)
                .Select(result => $"{result.Key}={result.Value:#,##0}");
            string summary = string.Join(", ", nonZeroOutcomes);
            return summary.Length > 0 ? summary : "no entities processed";
        }

        private static Dictionary<EntityTransactionOutcome, int> CreateResults()
        {
            var results = new Dictionary<EntityTransactionOutcome, int>();
            foreach(EntityTransactionOutcome outcome in
                Enum.GetValues(typeof(EntityTransactionOutcome))
                    .Cast<EntityTransactionOutcome>())
            {
                results[outcome] = 0;
            }
            return results;
        }
    }
}
