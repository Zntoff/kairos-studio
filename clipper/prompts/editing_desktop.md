<!-- version: 2 -->
## Desktop creative edition — additional instructions

Make a distinct editorial piece, not a captioned excerpt. Establish a specific setup, build anticipation,
then show the payoff and explain something a viewer can learn or notice. Describe this original angle
in `editorial.original_angle`, and justify the rhythm in `editorial.reason`. Never claim the edit meets
YouTube monetization rules: effects and AI text alone are not proof of originality or permission.

Choose `editorial.pacing=dynamic` for reactions, humor, action, fails, excitement and demonstrations.
Use 2–5 motivated visual beats and 2–4 licensed sound accents, varied in timing and intensity. Connect
them to what actually happens: an anticipation push-in, reaction emphasis, an impact beat, a payoff.
Avoid effects at arbitrary fixed intervals. Choose `calm` only for genuinely quiet explanations;
explain why restraint is appropriate. Do not fabricate a reaction or event to satisfy an effect count.

Your original commentary must explain a concrete observation, cause, contrast or useful detail visible
in the supplied material; it must add to the source rather than repeat the dialogue. Aim for 20–45 words
in total per language across 2–3 concise notes, with no fabricated background facts. Keep within the
individual note character limits. A hook is not original analysis by itself.

### Faces and reactions

First plan the MAIN framing in `framing`. Prefer `mode=fit` for screen recordings and gameplay, multiple windows,
text interfaces, a creator webcam in a corner, or action spread across the frame. This preserves the
whole video. Choose `subject` only when all essential content fits in one vertical crop; provide a
source `subject_region` enclosing that content, `frame_index` and `reason`. Never assume crop_x=0.5
keeps the important subject. On a 1080×1920 output the shared conservative work area is x=100..980,
y=240..1680. Keep critical faces, hands, action and text inside it, away from bottom captions and the
right button rail. The program centers the subject in this area, accounts for camera zoom/shake and
uses full-frame fit in auto mode if the subject cannot fit. An explicit vertical crop stays vertical.
When the owner requests a vertical crop, always identify the critical subject_region and frame_index
so the code can center it. For a moving primary face or subject, also return
`framing.subject_track`: one {frame_index, region:{x,y,w,h}} for each supplied image where that
same subject is visible. The renderer smoothly interpolates these grounded positions for vertical
tracking. Never invent positions for unseen frames or switch to a different person. If there are
fewer than two reliable positions, omit the track and use the single grounded crop. These are working margins, not universal official organic-platform specs.

Images are in order and the input lists each `frame_index` and source time. Inspect ALL of them. When a
visible human face has a meaningful reaction, add `face_focus` to effects with a `region` containing
normalized x,y,w,h in the ORIGINAL source image, its reference `frame_index`, a brief `reason`, and a
nearby `at_word`. Include the head and shoulders with room for small movement. This creates a large
animated face inset, keeping the underlying footage/context and subtitles. This is not face tracking.
Use 0.6–2 seconds; at most 3, never overlapping. Do not select a game character, avatar or drawing, or pretend a face is
visible if it is too small, occluded, off-screen or unclear. If there is no usable face, use a regular
camera effect on the actual action. Never invent face coordinates from the transcript alone.

Use a padded head-and-shoulders region, never a tight eye or mouth box. The entire rectangle is
displayed at its own aspect ratio, without a square center-crop, and placed above the primary action.

### Sound and motion graphics

Use meaningful library SFX: a short whoosh for a push-in, a soft click/ding for an observation, an impact
for a genuine impact, a boing for an actual comic fail. Use `audio.sfx[].gain` around 0.4–0.8 so speech
stays understandable. Never add a fake spoken reaction or drown the speaker. Music is optional.

Use `texts` for 1–3 SHORT callouts (1–5 words) that point out the relevant action or explain the payoff.
Each requires localized `text`, `at_word`, `duration`, a `preset` (label/comic/hand/neon) and `anim_in`
(pop/zoom/slide_up/slide_left). They animate separately from subtitles. Avoid repeating either the hook,
captions or commentary. Match the style to the scene and keep them brief and readable.

Effects may specify `intensity` between 0.2 and 2. Use restrained variations, not maximum intensity.
No strobing: at most one brief flash, only for a justified reveal. Do not use blur as decoration.

Return ALL of `editorial`, `effects`, `audio`, `texts` even if a calm scene warrants empty arrays.

Also return `framing`. Use `record_scratch_cc0` for a genuine reversal or awkward interruption,
`rimshot_cc0` sparingly after an actual joke, and `airhorn_cc0` at low gain for an earned celebratory
payoff, only if these IDs appear in the supplied licensed library. Never replace every beat with a
meme accent or use someone else's spoken catchphrase. A free download is not proof of a license.
