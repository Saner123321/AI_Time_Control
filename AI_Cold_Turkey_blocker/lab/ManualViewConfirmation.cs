using System;
using System.Drawing;

// Only an actual confirmed document observation renews this lease. A valid allowed
// page replaces the old blocked page immediately; uncertain polling cannot renew it.
internal sealed class ManualViewConfirmation {
    internal const long LeaseMs=2500;
    BrowserProbeResult confirmed;long at;
    internal void Observe(BrowserProbeResult sample,long now){
        if(sample.State=="committed_document_urls_observed"&&!sample.Truncated&&sample.DurationMs<=1200){confirmed=sample;at=now;return;}
        bool temporary=sample.State=="committed_document_url_unavailable"||sample.State=="probe_timeout"||sample.State=="probe_failed"||sample.State=="probe_unavailable"||sample.State=="probe_output_incomplete";
        if(!temporary||sample.HiddenDocuments>0||sample.Truncated)Clear();
    }
    internal BrowserProbeResult Select(long now,long foreground,Rectangle owner,out string reason){
        if(confirmed==null){reason="no_confirmed_document";return null;}
        if(now<at||now-at>LeaseMs){reason="confirmation_expired";return null;}
        if(foreground!=confirmed.Window){reason="target_not_foreground";return null;}
        var expected=new Rectangle(confirmed.OwnerX,confirmed.OwnerY,confirmed.OwnerWidth,confirmed.OwnerHeight);
        if(owner!=expected){reason="owner_geometry_changed";return null;}
        reason="confirmed_document";return confirmed;
    }
    internal void Clear(){confirmed=null;}
}
