use airplay_core::{AirPlayError, Result, SampleRate};
use airplay_resampler::StereoResampler;
use crossbeam_channel::Sender;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::Arc;
use wasapi::{DeviceCollection, Direction, SampleType, StreamMode, WaveFormat};

/// Windows WASAPI loopback capture feeding interleaved stereo f32 frames at `target_rate` (44.1 kHz or 48 kHz).
pub struct WasapiLoopbackCapture {
    stop: Arc<AtomicBool>,
    handle: Option<std::thread::JoinHandle<()>>,
}

impl WasapiLoopbackCapture {
    pub fn start_loopback(
        device_id: Option<String>,
        target_rate: SampleRate,
        tx: Sender<Vec<f32>>,
    ) -> Result<Self> {
        let stop = Arc::new(AtomicBool::new(false));
        let stop_c = stop.clone();

        let handle = std::thread::Builder::new()
            .name("wasapi-loopback".into())
            .spawn(move || {
                if let Err(e) = run_capture_loop(device_id.as_deref(), target_rate, tx, stop_c) {
                    tracing::error!("WASAPI capture loop exited with error: {e}");
                }
            })?;

        Ok(Self {
            stop,
            handle: Some(handle),
        })
    }

    pub fn stop(&mut self) {
        self.stop.store(true, Ordering::Relaxed);
        if let Some(h) = self.handle.take() {
            let _ = h.join();
        }
    }
}

impl Drop for WasapiLoopbackCapture {
    fn drop(&mut self) {
        self.stop();
    }
}

fn select_render_device(device_id: Option<&str>) -> Result<wasapi::Device> {
    if let Some(want) = device_id.map(str::trim).filter(|s| !s.is_empty()) {
        if let Ok(col) = DeviceCollection::new(&Direction::Render) {
            if let Ok(count) = col.get_nbr_devices() {
                for i in 0..count {
                    if let Ok(dev) = col.get_device_at_index(i) {
                        let id_match = dev.get_id().ok().as_deref() == Some(want);
                        let name_match = dev
                            .get_friendlyname()
                            .ok()
                            .map(|n| n.eq_ignore_ascii_case(want))
                            .unwrap_or(false);
                        if id_match || name_match {
                            tracing::info!("Selected requested WASAPI render device: {want}");
                            return Ok(dev);
                        }
                    }
                }
            }
        }
        tracing::warn!("Requested WASAPI device '{want}' not found; falling back to default render device");
    }
    wasapi::get_default_device(&Direction::Render)
        .map_err(|e| AirPlayError::Audio(format!("WASAPI get_default_device failed: {e}")))
}

fn run_capture_loop(
    device_id: Option<&str>,
    target_rate: SampleRate,
    tx: Sender<Vec<f32>>,
    stop: Arc<AtomicBool>,
) -> Result<()> {
    let _ = wasapi::initialize_mta();
    let device = select_render_device(device_id)?;
    let mut audio_client = device
        .get_iaudioclient()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI get_iaudioclient failed: {e}")))?;
    let mix_fmt = audio_client
        .get_mixformat()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI get_mixformat failed: {e}")))?;
    let src_rate = mix_fmt.get_samplespersec();
    let desired = WaveFormat::new(32, 32, &SampleType::Float, src_rate as usize, 2, None);
    let (_def_time, min_time) = audio_client
        .get_device_period()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI get_device_period failed: {e}")))?;
    let mode = StreamMode::EventsShared {
        autoconvert: true,
        buffer_duration_hns: min_time,
    };
    audio_client
        .initialize_client(&desired, &Direction::Capture, &mode)
        .map_err(|e| AirPlayError::Audio(format!("WASAPI initialize_client failed: {e}")))?;
    let h_event = audio_client
        .set_get_eventhandle()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI event handle failed: {e}")))?;
    let capture_client = audio_client
        .get_audiocaptureclient()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI capture client failed: {e}")))?;
    audio_client
        .start_stream()
        .map_err(|e| AirPlayError::Audio(format!("WASAPI start_stream failed: {e}")))?;

    let mut resampler = StereoResampler::new(src_rate, target_rate)?;
    let mut raw_buf = std::collections::VecDeque::with_capacity(48_000 * 8);
    let block_align = desired.get_blockalign() as usize;
    tracing::info!(
        "WASAPI loopback capture started: {} Hz -> {} Hz (block_align={})",
        src_rate,
        target_rate.hz(),
        block_align
    );

    while !stop.load(Ordering::Relaxed) {
        let _ = h_event.wait_for_event(20);
        let _ = capture_client.read_from_device_to_deque(&mut raw_buf);
        if raw_buf.len() < block_align {
            continue;
        }
        let avail_bytes = (raw_buf.len() / block_align) * block_align;
        let bytes: Vec<u8> = raw_buf.drain(..avail_bytes).collect();
        let mut f32_samples = Vec::with_capacity(bytes.len() / 4);
        for chunk in bytes.chunks_exact(4) {
            f32_samples.push(f32::from_le_bytes([chunk[0], chunk[1], chunk[2], chunk[3]]));
        }
        if let Ok(resampled) = resampler.process_interleaved(&f32_samples) {
            if !resampled.is_empty() {
                let _ = tx.try_send(resampled);
            }
        }
    }
    let _ = audio_client.stop_stream();
    Ok(())
}
