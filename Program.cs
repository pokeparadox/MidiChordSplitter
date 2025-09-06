using Melanchall.DryWetMidi.Interaction;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Common;
using ZLinq;

namespace MidiChordSplitter
{
    class Program
    {
        static CmdArgs ParseArgs(string[] args)
        {
            var argObj = new CmdArgs();
            for (int i = 0; i < args.Length; ++i)
            {
                if (args[i] == "-i" && i + 1 < args.Length)
                {
                    argObj.InputFile = args[++i];
                }
                else if (args[i] == "-o" && i + 1 < args.Length)
                {
                    argObj.OutputFile = args[++i];
                }
            }
            return argObj;
        }
        
        static void Main(string[] args)
        {
            var cmd = ParseArgs(args);
            string inputFilePath = string.IsNullOrEmpty(cmd.InputFile) ? "./input.mid" : cmd.InputFile;
            string outputFilePath = string.IsNullOrEmpty(cmd.OutputFile) ? $"{inputFilePath}.split.mid" : cmd.OutputFile;

            var midiFile = MidiFile.Read(inputFilePath);
            if (midiFile == null)
            {
                Console.WriteLine("Failed to read MIDI file.");
                return;
            }

            var chordSettings = new ChordDetectionSettings { NotesTolerance = 3, NotesMinCount = 2 };
            var trackChunks = midiFile
                .GetTrackChunks()
                .ToList();
            var allChannels = Enumerable
                .Range(0, 16)
                .Select(i => new FourBitNumber((byte)i))
                .ToList();
            
            BuildProgramChangeTimelineAndInstrumentMap(trackChunks, out var channelProgramTimes, out var instrumentChannels);
            var channelNoteTimes = BuildChannelNoteTimes(trackChunks);
            
            // Collections for new notes and program changes to insert
            var newNotes = new List<(Note note, FourBitNumber channel, long time, TrackChunk chunk, SevenBitNumber program)>();
            var programChanges = new HashSet<(TrackChunk chunk, FourBitNumber channel, SevenBitNumber program, long time)>();
            ProcessChunksAndSplitChords(trackChunks, chordSettings, channelProgramTimes, instrumentChannels, channelNoteTimes, allChannels, newNotes, programChanges);
            InsertProgramChanges(programChanges);
            InsertNewNotes(newNotes);
            midiFile.Write(outputFilePath, overwriteFile: true);
            Console.WriteLine($"Wrote: {outputFilePath}");
        }

        private static void ProcessChunksAndSplitChords(List<TrackChunk> trackChunks, ChordDetectionSettings chordSettings, Dictionary<FourBitNumber, List<(long time, SevenBitNumber program)>> channelProgramTimes, Dictionary<SevenBitNumber, List<FourBitNumber>> instrumentChannels, Dictionary<FourBitNumber, List<(long start, long end)>> channelNoteTimes, List<FourBitNumber> allChannels, List<(Note note, FourBitNumber channel, long time, TrackChunk chunk, SevenBitNumber program)> newNotes, HashSet<(TrackChunk chunk, FourBitNumber channel, SevenBitNumber program, long time)> programChanges)
        {
            foreach (var chunk in trackChunks)
            {
                var chords = chunk.GetChords(chordSettings).ToList();
                var notesToRemove = new List<Note>();

                foreach (var chord in chords)
                {
                    var chordNotes = chord.Notes
                        .AsValueEnumerable()
                        .OrderBy(n => n.NoteNumber)
                        .ToList();
                    if (chordNotes.Count < 2)
                    {
                        continue;
                    }

                    // Remove all chord notes from the original chunk
                    notesToRemove.AddRange(chordNotes);

                    var mainProgram = GetProgramForChannelAtTime(chordNotes[0].Channel, chordNotes[0].Time, channelProgramTimes);
                    var usedChannels = new HashSet<FourBitNumber>();

                    foreach (var note in chordNotes)
                    {
                        FourBitNumber? allocated = null;
                        // Try to allocate to any channel for this instrument that is not busy at this time
                        if (instrumentChannels.TryGetValue(mainProgram, out var existing))
                        {
                            foreach (var ch in existing)
                            {
                                if (usedChannels.Contains(ch))
                                {
                                    continue;
                                }
                                var overlaps = channelNoteTimes.ContainsKey(ch) && channelNoteTimes[ch].Any(t => note.Time < t.end && note.Time + note.Length > t.start);
                                if (!overlaps)
                                {
                                    allocated = ch;
                                    break;
                                }
                            }
                        }
                        // If not available, allocate to a new channel
                        if (allocated == null)
                        {
                            foreach (var ch in allChannels)
                            {
                                if (usedChannels.Contains(ch))
                                {
                                    continue;
                                }
                                if (instrumentChannels.TryGetValue(mainProgram, out var ex) && ex.Contains(ch))
                                {
                                    continue; // already checked
                                }
                                var overlaps = channelNoteTimes.ContainsKey(ch) && channelNoteTimes[ch].Any(t => note.Time < t.end && note.Time + note.Length > t.start);
                                if (!overlaps)
                                {
                                    allocated = ch;
                                    // Register this channel for this instrument
                                    if (!instrumentChannels.ContainsKey(mainProgram))
                                    {
                                        instrumentChannels[mainProgram] = new List<FourBitNumber>();
                                    }
                                    instrumentChannels[mainProgram].Add(ch);
                                    break;
                                }
                            }
                        }
                        if (allocated == null)
                        {
                            Console.WriteLine($"Warning: not enough channels to split chord at time {chord.Time}. Discarding note {note.NoteNumber}.");
                            continue;
                        }
                        // Always ensure correct program change for this channel at this time
                        programChanges.Add((chunk, allocated.Value, mainProgram, note.Time));
                        newNotes.Add((note, allocated.Value, note.Time, chunk, mainProgram));
                        usedChannels.Add(allocated.Value);
                        // Update occupancy
                        if (!channelNoteTimes.ContainsKey(allocated.Value))
                        {
                            channelNoteTimes[allocated.Value] = new List<(long, long)>();
                        }
                        channelNoteTimes[allocated.Value].Add((note.Time, note.Time + note.Length));
                    }
                }

                // remove original chord notes from this chunk
                if (notesToRemove.Count > 0)
                {
                    // Remove Note objects
                    var mgr = chunk.ManageNotes();
                    foreach (var note in notesToRemove.Distinct())
                    {
                        mgr.Objects.Remove(note);
                    }
                    mgr.SaveChanges();

                    // Remove corresponding TimedEvents (NoteOn/NoteOff)
                    var timedMgr = chunk.ManageTimedEvents();
                    var noteOnsOffs = timedMgr.Objects
                        .Where(te => (te.Event is NoteOnEvent || te.Event is NoteOffEvent) &&
                                     notesToRemove.Any(n => n.NoteNumber == ((NoteEvent)te.Event).NoteNumber && n.Channel == ((NoteEvent)te.Event).Channel && n.Time == te.Time))
                        .ToList();
                    foreach (var te in noteOnsOffs)
                    {
                        timedMgr.Objects.Remove(te);
                    }
                    timedMgr.SaveChanges();
                }
            }
        }

        private static void InsertProgramChanges(HashSet<(TrackChunk chunk, FourBitNumber channel, SevenBitNumber program, long time)> programChanges)
        {
            foreach (var pc in programChanges.OrderBy(p => p.time))
            {
                var mgr = pc.chunk.ManageTimedEvents();
                mgr.Objects.Add(new TimedEvent(new ProgramChangeEvent(pc.program) { Channel = pc.channel }, pc.time));
                mgr.SaveChanges();
            }
        }

        private static void InsertNewNotes(List<(Note note, FourBitNumber channel, long time, TrackChunk chunk, SevenBitNumber program)> newNotes)
        {
            foreach (var nn in newNotes.OrderBy(n => n.time))
            {
                var clone = nn.note.Clone() as Note;
                if (clone == null)
                    continue;
                clone.Channel = nn.channel;
                var on = clone.GetTimedNoteOnEvent();
                var off = clone.GetTimedNoteOffEvent();
                var mgr = nn.chunk.ManageTimedEvents();
                mgr.Objects.Add(on);
                mgr.Objects.Add(off);
                mgr.SaveChanges();
            }
        }

        private static Dictionary<FourBitNumber, List<(long start, long end)>> BuildChannelNoteTimes(List<TrackChunk> trackChunks)
        {
            var channelNoteTimes = new Dictionary<FourBitNumber, List<(long start, long end)>>();
            foreach (var chunk in trackChunks)
            {
                foreach (var note in chunk.GetNotes())
                {
                    if (!channelNoteTimes.ContainsKey(note.Channel))
                    {
                        channelNoteTimes[note.Channel] = new List<(long, long)>();
                    }
                    channelNoteTimes[note.Channel].Add((note.Time, note.Time + note.Length));
                }
            }

            return channelNoteTimes;
        }

        private static SevenBitNumber GetProgramForChannelAtTime(FourBitNumber channel, long time, Dictionary<FourBitNumber, List<(long time, SevenBitNumber program)>> channelProgramTimes)
        {
            if (!channelProgramTimes.TryGetValue(channel, out var list) || list.Count == 0)
            {
                return SevenBitNumber.MinValue;
            }
            var prior = list
                .AsValueEnumerable()
                .Where(p => p.time <= time)
                .OrderByDescending(p => p.time)
                .FirstOrDefault();
            if (!prior.Equals(default((long, SevenBitNumber))))
            {
                return prior.program;
            }
            return list
                .AsValueEnumerable()
                .OrderBy(p => p.time)
                .First()
                .program;
        }
        
        private static void BuildProgramChangeTimelineAndInstrumentMap(List<TrackChunk> trackChunks, out Dictionary<FourBitNumber, List<(long time, SevenBitNumber program)>> channelProgramTimes, out Dictionary<SevenBitNumber, List<FourBitNumber>> instrumentChannels)
        {
            channelProgramTimes = new();
            instrumentChannels = new();
            foreach (var chunk in trackChunks)
            {
                var timedProgramChanges = chunk.GetTimedEvents().Where(te => te.Event is ProgramChangeEvent).ToList();
                foreach (var te in timedProgramChanges)
                {
                    var pc = (ProgramChangeEvent)te.Event;
                    if (!channelProgramTimes.ContainsKey(pc.Channel))
                    {
                        channelProgramTimes[pc.Channel] = new List<(long, SevenBitNumber)>();
                    }
                    channelProgramTimes[pc.Channel].Add((te.Time, pc.ProgramNumber));

                    if (!instrumentChannels.ContainsKey(pc.ProgramNumber))
                    {
                        instrumentChannels[pc.ProgramNumber] = new List<FourBitNumber>();
                    }

                    if (!instrumentChannels[pc.ProgramNumber].Contains(pc.Channel))
                    {
                        instrumentChannels[pc.ProgramNumber].Add(pc.Channel);
                    }
                }
            }
        }
    }
}
