<script>
  import { cubicOut } from 'svelte/easing';
  import { prefersReducedMotion, tweened } from 'svelte/motion';

  export let label;
  export let value;

  const animationDuration = 500;
  const displayedValue = tweened(value, {
    duration: animationDuration,
    easing: cubicOut
  });

  $: displayedValue.set(value, {
    duration: prefersReducedMotion.current ? 0 : animationDuration
  });

  function formatCount(count) {
    return Math.round(count).toLocaleString();
  }
</script>

<article class="card metric {label.toLowerCase()}">
  <span class="metric-label">{label}</span>
  <strong>{formatCount($displayedValue)}</strong>
</article>

<style>
  article {
    animation: none;
  }
</style>
