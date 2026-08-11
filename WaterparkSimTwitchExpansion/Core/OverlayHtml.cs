namespace WaterparkSimTwitchExpansion.Core
{
    /// <summary>The static page OverlayServer serves at "/overlay.html" - see that class for why this exists.</summary>
    public static class OverlayHtml
    {
        public const string Page = @"<!DOCTYPE html>
<html>
<head>
<meta charset='utf-8'>
<title>Waterpark Twitch Chaos Overlay</title>
<style>
  html, body { margin:0; padding:0; background: transparent; overflow: hidden; font-family: 'Segoe UI', Arial, sans-serif; }

  #feed {
    position: fixed; left: 24px; bottom: 24px;
    display: flex; flex-direction: column-reverse; gap: 10px;
  }

  .toast {
    display: flex; align-items: center; gap: 12px;
    background: linear-gradient(135deg, #22d3ee, #0369a1);
    color: white; padding: 14px 22px; border-radius: 999px;
    box-shadow: 0 6px 20px rgba(0,0,0,0.35);
    font-size: 22px; font-weight: 700;
    border: 3px solid rgba(255,255,255,0.55);
    opacity: 0; transform: translateX(-50px);
    animation: splash-in 0.4s ease-out forwards, splash-out 0.5s ease-in 4.5s forwards;
    max-width: 560px;
  }

  .toast .icon { font-size: 30px; }
  .toast .amount { opacity: 0.85; font-weight: 500; font-size: 18px; margin-left: auto; white-space: nowrap; }

  .avatar-wrap { position: relative; width: 44px; height: 44px; flex-shrink: 0; }
  .avatar-wrap .avatar {
    width: 44px; height: 44px; border-radius: 50%; object-fit: cover; display: block;
    border: 2px solid rgba(255,255,255,0.85);
  }
  .avatar-wrap .badge {
    position: absolute; right: -4px; bottom: -4px;
    width: 20px; height: 20px; border-radius: 50%;
    background: #0369a1; border: 2px solid white;
    display: flex; align-items: center; justify-content: center;
    font-size: 12px; line-height: 1;
  }

  @keyframes splash-in { to { opacity: 1; transform: translateX(0); } }
  @keyframes splash-out { to { opacity: 0; transform: translateX(-50px) scale(0.9); } }

  .poll {
    position: fixed; top: 24px; left: 50%; transform: translateX(-50%);
    background: linear-gradient(135deg, #0369a1, #0c4a6e);
    color: white; border-radius: 18px; padding: 16px 22px;
    box-shadow: 0 8px 24px rgba(0,0,0,0.4);
    border: 3px solid rgba(255,255,255,0.55);
    min-width: 320px; max-width: 480px;
    opacity: 0; transform: translateX(-50%) translateY(-20px);
    animation: poll-in 0.4s ease-out forwards;
  }
  .poll.poll-fade-out { animation: poll-out 0.5s ease-in forwards; }

  .poll-header { font-size: 20px; font-weight: 800; text-align: center; margin-bottom: 4px; }
  .poll-timer { font-size: 13px; opacity: 0.85; text-align: center; margin-bottom: 10px; }

  .poll-options { display: flex; flex-direction: column; gap: 8px; }
  .poll-option {
    position: relative; background: rgba(255,255,255,0.12);
    border-radius: 10px; padding: 8px 12px; overflow: hidden;
    display: flex; align-items: center; justify-content: space-between;
  }
  .poll-option.poll-winner { outline: 3px solid #ffd23e; }

  .poll-bar {
    position: absolute; left: 0; top: 0; bottom: 0;
    background: rgba(34,211,238,0.45); width: 0%;
    transition: width 0.3s ease-out; z-index: 0;
  }
  .poll-label { position: relative; z-index: 1; font-weight: 600; }
  .poll-count { position: relative; z-index: 1; font-weight: 800; margin-left: 10px; white-space: nowrap; }

  @keyframes poll-in { to { opacity: 1; transform: translateX(-50%) translateY(0); } }
  @keyframes poll-out { to { opacity: 0; transform: translateX(-50%) translateY(-20px) scale(0.9); } }
</style>
</head>
<body>
<div id='feed'></div>
<script>
  var ICONS = {
    yeet: '🚀', poop: '💩', break: '🌊', ragdoll: '🤸',
    invert: '🔄', nojump: '🚫', drop: '📦',
    vomit: '🤮', pee: '💦', trash: '🗑️'
  };

  var pollWidget = null;
  var pollCountdownInterval = null;

  function clearPollWidget() {
    if (pollWidget) { pollWidget.remove(); pollWidget = null; }
    if (pollCountdownInterval) { clearInterval(pollCountdownInterval); pollCountdownInterval = null; }
  }

  function setPollTimerText(seconds) {
    var el = document.getElementById('poll-timer');
    if (el) { el.textContent = Math.max(0, Math.round(seconds)) + 's left - type a number to vote!'; }
  }

  function showPoll(data) {
    clearPollWidget();

    var widget = document.createElement('div');
    widget.className = 'poll';

    var header = document.createElement('div');
    header.className = 'poll-header';
    header.textContent = 'CHAOS VOTE!';
    widget.appendChild(header);

    var timer = document.createElement('div');
    timer.className = 'poll-timer';
    timer.id = 'poll-timer';
    widget.appendChild(timer);

    var optionsEl = document.createElement('div');
    optionsEl.className = 'poll-options';
    data.options.forEach(function (desc, i) {
      var row = document.createElement('div');
      row.className = 'poll-option';
      row.id = 'poll-option-' + i;

      var bar = document.createElement('div');
      bar.className = 'poll-bar';
      bar.id = 'poll-bar-' + i;

      var label = document.createElement('span');
      label.className = 'poll-label';
      label.textContent = (i + 1) + ') ' + desc;

      var count = document.createElement('span');
      count.className = 'poll-count';
      count.id = 'poll-count-' + i;
      count.textContent = '0';

      row.appendChild(bar);
      row.appendChild(label);
      row.appendChild(count);
      optionsEl.appendChild(row);
    });
    widget.appendChild(optionsEl);

    document.body.appendChild(widget);
    pollWidget = widget;

    var remaining = data.durationSeconds;
    setPollTimerText(remaining);
    pollCountdownInterval = setInterval(function () {
      remaining -= 1;
      setPollTimerText(remaining);
      if (remaining <= 0) { clearInterval(pollCountdownInterval); }
    }, 1000);
  }

  function updatePollVotes(data) {
    if (!pollWidget) { return; }
    var total = data.counts.reduce(function (sum, c) { return sum + c; }, 0) || 1;
    data.counts.forEach(function (c, i) {
      var countEl = document.getElementById('poll-count-' + i);
      var barEl = document.getElementById('poll-bar-' + i);
      if (countEl) { countEl.textContent = c; }
      if (barEl) { barEl.style.width = Math.round((c / total) * 100) + '%'; }
    });
  }

  function endPoll(data) {
    if (!pollWidget) { return; }
    if (pollCountdownInterval) { clearInterval(pollCountdownInterval); pollCountdownInterval = null; }

    updatePollVotes(data);

    var header = pollWidget.querySelector('.poll-header');
    if (header) { header.textContent = data.winnerIndex >= 0 ? 'Vote result!' : 'No votes - maybe next time!'; }
    var timerEl = document.getElementById('poll-timer');
    if (timerEl) { timerEl.remove(); }
    if (data.winnerIndex >= 0) {
      var winnerRow = document.getElementById('poll-option-' + data.winnerIndex);
      if (winnerRow) { winnerRow.classList.add('poll-winner'); }
    }

    var widgetRef = pollWidget;
    pollWidget = null;
    setTimeout(function () {
      widgetRef.classList.add('poll-fade-out');
      setTimeout(function () { widgetRef.remove(); }, 600);
    }, 4000);
  }

  function addToast(data) {
    var feed = document.getElementById('feed');
    var toast = document.createElement('div');
    toast.className = 'toast';

    if (data.avatarUrl) {
      var avatarWrap = document.createElement('span');
      avatarWrap.className = 'avatar-wrap';

      var avatar = document.createElement('img');
      avatar.className = 'avatar';
      avatar.src = data.avatarUrl;
      avatar.referrerPolicy = 'no-referrer';
      avatar.onerror = function () {
        // Broken/expired avatar URL - fall back to the plain icon look instead of a broken image.
        avatarWrap.remove();
        toast.insertBefore(makeIcon(data), toast.firstChild);
      };

      var badge = document.createElement('span');
      badge.className = 'badge';
      badge.textContent = ICONS[data.action] || '🎉';

      avatarWrap.appendChild(avatar);
      avatarWrap.appendChild(badge);
      toast.appendChild(avatarWrap);
    } else {
      toast.appendChild(makeIcon(data));
    }

    var text = document.createElement('span');
    text.textContent = data.displayName + ' ' + data.description + '!';

    var amount = document.createElement('span');
    amount.className = 'amount';
    amount.textContent = '-' + data.cost + ' pts';

    toast.appendChild(text);
    toast.appendChild(amount);
    feed.appendChild(toast);

    setTimeout(function () { toast.remove(); }, 5000);
    while (feed.children.length > 5) {
      feed.removeChild(feed.firstChild);
    }
  }

  function makeIcon(data) {
    var icon = document.createElement('span');
    icon.className = 'icon';
    icon.textContent = ICONS[data.action] || '🎉';
    return icon;
  }

  function connect() {
    var source = new EventSource('/events');
    source.addEventListener('redemption', function (e) {
      addToast(JSON.parse(e.data));
    });
    source.addEventListener('poll_started', function (e) {
      showPoll(JSON.parse(e.data));
    });
    source.addEventListener('poll_votes', function (e) {
      updatePollVotes(JSON.parse(e.data));
    });
    source.addEventListener('poll_ended', function (e) {
      endPoll(JSON.parse(e.data));
    });
    source.onerror = function () {
      source.close();
      setTimeout(connect, 2000);
    };
  }

  connect();
</script>
</body>
</html>";
    }
}
