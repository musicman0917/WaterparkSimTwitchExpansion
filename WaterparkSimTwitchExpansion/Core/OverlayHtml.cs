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

  @keyframes splash-in { to { opacity: 1; transform: translateX(0); } }
  @keyframes splash-out { to { opacity: 0; transform: translateX(-50px) scale(0.9); } }
</style>
</head>
<body>
<div id='feed'></div>
<script>
  var ICONS = {
    yeet: '🚀', poop: '💩', break: '🌊', ragdoll: '🤸',
    invert: '🔄', nojump: '🚫', drop: '📦'
  };

  function addToast(data) {
    var feed = document.getElementById('feed');
    var toast = document.createElement('div');
    toast.className = 'toast';

    var icon = document.createElement('span');
    icon.className = 'icon';
    icon.textContent = ICONS[data.action] || '🎉';

    var text = document.createElement('span');
    text.textContent = data.displayName + ' ' + data.description + '!';

    var amount = document.createElement('span');
    amount.className = 'amount';
    amount.textContent = '-' + data.cost + ' pts';

    toast.appendChild(icon);
    toast.appendChild(text);
    toast.appendChild(amount);
    feed.appendChild(toast);

    setTimeout(function () { toast.remove(); }, 5000);
    while (feed.children.length > 5) {
      feed.removeChild(feed.firstChild);
    }
  }

  function connect() {
    var source = new EventSource('/events');
    source.addEventListener('redemption', function (e) {
      addToast(JSON.parse(e.data));
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
