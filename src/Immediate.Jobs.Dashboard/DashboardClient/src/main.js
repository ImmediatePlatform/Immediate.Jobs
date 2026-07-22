import './app.css';
import './batches.css';
import App from './App.svelte';
import { mount } from 'svelte';

mount(App, { target: document.querySelector('#app') });
