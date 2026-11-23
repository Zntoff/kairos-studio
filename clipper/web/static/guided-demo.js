'use strict';

const byId = id => document.getElementById(id);
const tabs = [...document.querySelectorAll('[data-step]')];
const panels = [...document.querySelectorAll('[data-panel]')];
const player = byId('player');
const sourceCaptions = byId('source-captions');
const scrubber = byId('scrubber');
const download = byId('download');
const moments = {
  ridge: { title: 'On the ridge', start: 0 },
  clouds: { title: 'Above the clouds', start: 8 },
};
let step = 0;
let unlocked = 0;
let approved = false;

function selected(name) {
  return document.querySelector(`input[name="${name}"]:checked`).value;
}

function updateTime() {
  const seconds = Math.floor(player.currentTime);
  scrubber.value = player.currentTime;
  byId('time').value = `0:${String(seconds).padStart(2, '0')} / 0:08`;
}

function showPreview() {
  const moment = selected('moment');
  const style = selected('style');
  const vertical = step >= 2;
  const file = vertical ? `demo-${moment}-${style}.mp4` : 'demo-source.mp4';
  const time = vertical ? player.currentTime : step === 1 ? moments[moment].start : 0;
  const oldWasVertical = byId('preview').classList.contains('vertical');
  byId('preview').classList.toggle('vertical', vertical);
  player.setAttribute('aria-label', vertical ? 'Vertical Short preview' : 'Sample source video');
  byId('timeline').hidden = !vertical;
  byId('media-name').textContent = vertical ? moments[moment].title : 'Ridge hike.mp4';
  byId('media-format').textContent = vertical ? '9:16 · 00:08' : '16:9 · 00:16';
  player.pause();
  const position = vertical && !oldWasVertical ? 0 : time;
  if (vertical) {
    player.removeAttribute('poster');
    sourceCaptions.remove();
  } else if (oldWasVertical) {
    player.poster = 'demo-poster.jpg';
    player.append(sourceCaptions);
  }
  if (player.getAttribute('src') !== file) player.src = file;
  player.currentTime = position;
  updateTime();
}

function showApproval() {
  const moment = selected('moment');
  const style = selected('style');
  byId('review-name').textContent = moments[moment].title;
  byId('review-details').textContent = `MP4 · 9:16 · ${style === 'clean' ? 'Clean' : 'Highlight'} captions`;
  byId('approve').hidden = approved;
  download.hidden = !approved;
  if (approved) {
    download.href = `demo-${moment}-${style}.mp4`;
    download.download = `kairos-demo-${moment}-${style}.mp4`;
  }
}

function showStep(next) {
  step = next;
  unlocked = Math.max(unlocked, step);
  tabs.forEach((tab, i) => {
    tab.disabled = i > unlocked;
    if (i === step) tab.setAttribute('aria-current', 'step');
    else tab.removeAttribute('aria-current');
    panels[i].hidden = i !== step;
  });
  byId('reset').hidden = unlocked === 0;
  showPreview();
  showApproval();
  byId('status').textContent = [
    'Ready to import sample video.',
    'Sample imported. Two moments are ready to preview.',
    'Sample Short generated. Try either caption style.',
    approved ? 'Approved. Your sample MP4 is ready to download.' : 'Ready for your review.',
  ][step];
  panels[step].querySelector('h3').focus({ preventScroll: true });
}

tabs.forEach((tab, i) => tab.addEventListener('click', () => showStep(i)));
byId('import').addEventListener('click', () => showStep(1));
byId('generate').addEventListener('click', () => showStep(2));
byId('review').addEventListener('click', () => showStep(3));
byId('approve').addEventListener('click', () => {
  approved = true;
  showApproval();
  byId('status').textContent = 'Approved. Your sample MP4 is ready to download.';
  download.focus();
});
byId('reset').addEventListener('click', () => {
  unlocked = 0;
  approved = false;
  document.querySelector('[name="moment"][value="ridge"]').checked = true;
  document.querySelector('[name="style"][value="clean"]').checked = true;
  showStep(0);
});
document.querySelectorAll('input[type="radio"]').forEach(input => input.addEventListener('change', () => {
  approved = false;
  showPreview();
  showApproval();
}));
player.addEventListener('timeupdate', updateTime);
player.addEventListener('loadedmetadata', updateTime);
scrubber.addEventListener('input', () => { player.currentTime = Number(scrubber.value); });
