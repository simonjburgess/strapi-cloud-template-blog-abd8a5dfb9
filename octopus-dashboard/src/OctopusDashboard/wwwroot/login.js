const reason = new URLSearchParams(location.search).get("error");
if (reason) {
  const el = document.getElementById("error");
  el.textContent = reason === "rate" ? "Too many attempts. Wait a minute and try again." : "That code isn't right.";
  el.hidden = false;
}
