(function () {
  var form = document.getElementById("prereg");
  if (!form) return;
  var msg = document.getElementById("prereg-msg");
  var btn = form.querySelector("button");
  var errors = {
    invalid_email: "That doesn't look like an email address.",
    consent_required: "Please tick the box so we know you want to hear from us.",
    challenge_failed: "The challenge didn't pass. Try again.",
  };
  function show(text, ok) { msg.textContent = text; msg.className = "msg " + (ok ? "ok" : "err"); }
  form.addEventListener("submit", function (e) {
    e.preventDefault();
    btn.disabled = true;
    show("", true);
    fetch(form.action, { method: "POST", body: new FormData(form) })
      .then(function (r) { return r.json(); })
      .then(function (d) {
        if (d.ok) {
          form.reset();
          show("Almost there. Check your inbox for a confirmation link.", true);
        } else {
          show(errors[d.error] || "Something went wrong. Please try again.", false);
        }
        if (window.turnstile) window.turnstile.reset();
      })
      .catch(function () { show("Couldn't reach the kingdom. Please try again.", false); })
      .then(function () { btn.disabled = false; });
  });
})();
