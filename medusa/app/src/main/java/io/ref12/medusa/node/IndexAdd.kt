package io.ref12.medusa.node

/** One "add this URL" for the paste box and the share dialog: a video, a playlist or a channel, with a priority, recorded in the local list. */
object IndexAdd {
    fun add(api: BlobApi, added: AddedItems, raw: String, opts: FollowOptions, priority: Int, nowMs: Long, channelId: (String) -> String?, videoChannel: (String) -> String?, alsoChannel: Boolean = false): String {
        val url = SourceUrl.firstUrl(raw) ?: raw.trim()
        if (SourceUrl.videoId(url) != null && !SourceUrl.isSource(url)) {
            val (res, ch) = IndexVideo.share(api, url, alsoChannel, nowMs, priority, videoChannel)
            val id = SourceUrl.videoId(url)!!
            if (res !is AddResult.Failed) added.add(AddedItem("video", "v-$id", "https://www.youtube.com/watch?v=$id", id, nowMs, priority))
            if (ch is FollowResult.Created) added.add(AddedItem(ch.ref.kind, ch.ref.jobId, url, ch.ref.id, nowMs, priority))
            return when (res) { AddResult.Added -> "Added"; AddResult.Already -> "Already indexed"; is AddResult.Failed -> "Could not add: " + res.message } +
                (ch?.let { "\n" + followText(it) } ?: "")
        }
        val res = SourceFollow.run(api, url, opts, nowMs, channelId)
        when (res) {
            is FollowResult.Created -> added.add(AddedItem(res.ref.kind, res.ref.jobId, url, SourceUrl.nameHint(url, res.ref), nowMs, priority))
            else -> {}
        }
        return followText(res)
    }

    fun followText(r: FollowResult): String = when (r) {
        is FollowResult.Created -> "Following " + r.ref.jobId
        is FollowResult.Exists -> "Already followed: " + r.ref.jobId
        FollowResult.NotASource -> "That is not a YouTube video, playlist or channel URL"
        is FollowResult.Failed -> "Could not follow: " + r.message
    }
}
